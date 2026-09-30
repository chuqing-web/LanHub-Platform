using System.Text;
using System.Text.Json;
using ArenaDuel.Gameplay;

namespace ArenaDuel.Online;

public static class MsgType
{
    public const byte Input = 1;
    public const byte Snapshot = 2;
    public const byte PickReady = 3;
    public const byte MatchStart = 4;
    public const byte MatchEnd = 5;
    public const byte PickSync = 6;
    public const byte Hello = 7;
    public const byte Rematch = 8;
}

public sealed class HelloMsg
{
    public string PlayerId { get; set; } = "";
    public string GameId { get; set; } = "arena-duel";
}

public sealed class PickReadyMsg
{
    public string PlayerId { get; set; } = "";
    public string CharacterId { get; set; } = "";
    public bool Ready { get; set; }
}

public sealed class MatchStartMsg
{
    public string ArenaId { get; set; } = "";
    public string PlayerIdA { get; set; } = "";
    public string PlayerIdB { get; set; } = "";
    public string CharacterIdA { get; set; } = "";
    public string CharacterIdB { get; set; } = "";
    public int Seed { get; set; }
}

public sealed class MatchEndMsg
{
    public string? WinnerPlayerId { get; set; }
    public string Reason { get; set; } = "";
    public float HpA { get; set; }
    public float HpB { get; set; }
}

public sealed class InputMsg
{
    public string PlayerId { get; set; } = "";
    public float MoveX { get; set; }
    public float MoveY { get; set; }
    public bool Attack { get; set; }
    public bool Skill1 { get; set; }
    public bool Skill2 { get; set; }
    public bool Ultimate { get; set; }
    public float AimX { get; set; }
    public float AimY { get; set; }
    public float WorldAimX { get; set; }
    public float WorldAimY { get; set; }
    public bool HasWorldAim { get; set; }
    public uint Seq { get; set; }
}

/// <summary>Wire-only fighter fields kept under UDP 1200-byte budget.</summary>
public sealed class WireFighter
{
    public string Pid { get; set; } = "";
    public string Cid { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Hp { get; set; }
    public float Mh { get; set; }
    public float Mp { get; set; }
    public float Mm { get; set; }
    public float R { get; set; }
    public float Ax { get; set; }
    public float Ay { get; set; }
    public float C1 { get; set; }
    public float C2 { get; set; }
    public float Cu { get; set; }
    public float Cl { get; set; }
    public int Cs { get; set; } = -1;
    public float Dot { get; set; }
}

public sealed class WireProj
{
    public int Id { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Vx { get; set; }
    public float Vy { get; set; }
    public float R { get; set; }
    public bool Aoe { get; set; }
}

public sealed class WireSnapshot
{
    public float T { get; set; }
    public string Aid { get; set; } = "";
    public string Ph { get; set; } = "battle";
    public string? W { get; set; }
    public string? Er { get; set; }
    public WireFighter A { get; set; } = new();
    public WireFighter B { get; set; } = new();
    public List<WireProj> P { get; set; } = [];
}

public static class NetCodec
{
    public const int MaxUnreliableBytes = 1200;

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static byte[] Encode<T>(byte type, T body)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body, Opts);
        var buf = new byte[1 + json.Length];
        buf[0] = type;
        Buffer.BlockCopy(json, 0, buf, 1, json.Length);
        return buf;
    }

    public static (byte type, string json) Decode(byte[] payload)
    {
        if (payload.Length < 1) return (0, "{}");
        return (payload[0], Encoding.UTF8.GetString(payload, 1, payload.Length - 1));
    }

    public static T? Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Opts);

    public static byte[] EncodeSnapshot(MatchSnapshot snap)
    {
        // Shrink projectile list until the frame fits the unreliable UDP budget.
        // Host also sends snapshots reliably; this keeps the UDP best-effort path valid.
        for (var take = Math.Min(8, snap.Projectiles.Count); take >= 0; take--)
        {
            var projs = snap.Projectiles
                .Take(take)
                .Select(p => new WireProj
                {
                    Id = p.Id,
                    X = p.X,
                    Y = p.Y,
                    Vx = p.Vx,
                    Vy = p.Vy,
                    R = p.Radius,
                    Aoe = p.IsAoePulse
                })
                .ToList();

            var wire = new WireSnapshot
            {
                T = snap.TimeLeft,
                Aid = snap.ArenaId,
                Ph = snap.Phase,
                W = snap.WinnerPlayerId,
                Er = string.IsNullOrEmpty(snap.EndReason) ? null : snap.EndReason,
                A = ToWire(snap.A),
                B = ToWire(snap.B),
                P = projs
            };
            var bytes = Encode(MsgType.Snapshot, wire);
            if (bytes.Length <= MaxUnreliableBytes || take == 0)
                return bytes;
        }

        // Unreachable — take==0 always returns above
        return Encode(MsgType.Snapshot, new WireSnapshot
        {
            T = snap.TimeLeft,
            Aid = snap.ArenaId,
            Ph = snap.Phase,
            A = ToWire(snap.A),
            B = ToWire(snap.B)
        });
    }

    public static MatchSnapshot? ParseSnapshot(string json)
    {
        var wire = Parse<WireSnapshot>(json);
        if (wire == null) return null;
        return new MatchSnapshot
        {
            TimeLeft = wire.T,
            ArenaId = wire.Aid,
            Phase = wire.Ph,
            WinnerPlayerId = wire.W,
            EndReason = wire.Er ?? "",
            A = FromWire(wire.A),
            B = FromWire(wire.B),
            Projectiles = wire.P.Select(p => new ProjectileState
            {
                Id = p.Id,
                X = p.X,
                Y = p.Y,
                Vx = p.Vx,
                Vy = p.Vy,
                Radius = p.R,
                IsAoePulse = p.Aoe
            }).ToList()
        };
    }

    public static byte[] EncodeInput(InputMsg msg) => Encode(MsgType.Input, msg);

    private static WireFighter ToWire(FighterState f) => new()
    {
        Pid = f.PlayerId,
        Cid = f.CharacterId,
        X = f.X,
        Y = f.Y,
        Hp = f.Hp,
        Mh = f.MaxHp,
        Mp = f.Mp,
        Mm = f.MaxMp,
        R = f.Radius,
        Ax = f.AimX,
        Ay = f.AimY,
        C1 = f.CdSkill1,
        C2 = f.CdSkill2,
        Cu = f.CdUltimate,
        Cl = f.CastLeft,
        Cs = f.CastingSlot,
        Dot = f.DotLeft
    };

    private static FighterState FromWire(WireFighter w) => new()
    {
        PlayerId = w.Pid,
        CharacterId = w.Cid,
        X = w.X,
        Y = w.Y,
        Hp = w.Hp,
        MaxHp = w.Mh,
        Mp = w.Mp,
        MaxMp = w.Mm,
        Radius = w.R,
        AimX = w.Ax,
        AimY = w.Ay,
        CdSkill1 = w.C1,
        CdSkill2 = w.C2,
        CdUltimate = w.Cu,
        CastLeft = w.Cl,
        CastingSlot = w.Cs,
        DotLeft = w.Dot
    };
}
