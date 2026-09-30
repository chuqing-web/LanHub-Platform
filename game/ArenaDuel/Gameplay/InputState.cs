namespace ArenaDuel.Gameplay;

public sealed class InputState
{
    public float MoveX { get; set; }
    public float MoveY { get; set; }
    public bool Attack { get; set; }
    /// <summary>True only on the frame the skill key was pressed (edge).</summary>
    public bool Skill1 { get; set; }
    public bool Skill2 { get; set; }
    public bool Ultimate { get; set; }
    /// <summary>Normalized aim direction (from fighter toward cursor).</summary>
    public float AimX { get; set; } = 1;
    public float AimY { get; set; }
    /// <summary>Cursor position in world coordinates.</summary>
    public float WorldAimX { get; set; }
    public float WorldAimY { get; set; }
    public bool HasWorldAim { get; set; }
    public uint Seq { get; set; }

    public InputState Clone() => new()
    {
        MoveX = MoveX,
        MoveY = MoveY,
        Attack = Attack,
        Skill1 = Skill1,
        Skill2 = Skill2,
        Ultimate = Ultimate,
        AimX = AimX,
        AimY = AimY,
        WorldAimX = WorldAimX,
        WorldAimY = WorldAimY,
        HasWorldAim = HasWorldAim,
        Seq = Seq
    };
}

public enum FighterSlot : byte
{
    A = 0,
    B = 1
}

public sealed class FighterState
{
    public string PlayerId { get; set; } = "";
    public string CharacterId { get; set; } = "";
    public FighterSlot Slot { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Hp { get; set; }
    public float MaxHp { get; set; }
    public float Mp { get; set; }
    public float MaxMp { get; set; }
    public float Radius { get; set; }
    public float MoveSpeed { get; set; }
    public float Shield { get; set; }
    public float CdAttack { get; set; }
    public float CdSkill1 { get; set; }
    public float CdSkill2 { get; set; }
    public float CdUltimate { get; set; }
    public float CastLeft { get; set; }
    public int CastingSlot { get; set; } = -1; // 0 atk 1 s1 2 s2 3 ult
    public float AimX { get; set; } = 1;
    public float AimY { get; set; }
    public float VelX { get; set; }
    public float VelY { get; set; }
    public float AttackLock { get; set; }
    public float IFrames { get; set; }
    public float SlowUntil { get; set; }
    public float SlowFactor { get; set; } = 1;
    public float RootUntil { get; set; }
    public float SilenceUntil { get; set; }
    public float AntiHealUntil { get; set; }
    public float MarkUntil { get; set; }
    public float MarkBonus { get; set; }
    public float DotLeft { get; set; }
    public float DotDps { get; set; }
    public bool Alive => Hp > 0;
}

public sealed class ProjectileState
{
    public int Id { get; set; }
    public FighterSlot Owner { get; set; }
    public string Type { get; set; } = "projectile";
    public float X { get; set; }
    public float Y { get; set; }
    public float Vx { get; set; }
    public float Vy { get; set; }
    public float Radius { get; set; }
    public float Damage { get; set; }
    public float Life { get; set; }
    public float LifeSteal { get; set; }
    public float SlowFactor { get; set; }
    public float SlowDuration { get; set; }
    public float SilenceDuration { get; set; }
    public float RootDuration { get; set; }
    public float AntiHealDuration { get; set; }
    public float DotDamage { get; set; }
    public float DotDuration { get; set; }
    public float MarkDuration { get; set; }
    public float MarkBonus { get; set; }
    public float ChainBonus { get; set; }
    public float Knockback { get; set; }
    public bool IsAoePulse { get; set; }
    public bool Hit { get; set; }
    public float PrevX { get; set; }
    public float PrevY { get; set; }
    public bool Pierce { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public HashSet<int> HitSlots { get; set; } = [];
}

public sealed class MatchSnapshot
{
    public float TimeLeft { get; set; }
    public string ArenaId { get; set; } = "";
    public string Phase { get; set; } = "battle"; // battle | ended
    public string? WinnerPlayerId { get; set; }
    public string EndReason { get; set; } = "";
    public FighterState A { get; set; } = new();
    public FighterState B { get; set; } = new();
    public List<ProjectileState> Projectiles { get; set; } = [];
}

public enum MatchEndReason
{
    None,
    Kill,
    Time,
    Draw,
    Disconnect
}
