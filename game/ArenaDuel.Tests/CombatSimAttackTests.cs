using System.IO;
using ArenaDuel.Data;
using ArenaDuel.Gameplay;
using Xunit;

namespace ArenaDuel.Tests;

public sealed class CombatSimAttackTests
{
    [Fact]
    public void Basic_attack_fires_out_of_melee_range_toward_aim()
    {
        GameData.Load(FindDataDir());
        var arena = GameData.Arenas.Arenas[0];
        var a = GameData.Characters.Characters.First(c => c.Id == "qinglan"); // ranged AA
        var b = GameData.Characters.Characters.First(c => c.Id == "akishun");
        var sim = new CombatSim(arena, a, b, "p1", "p2");

        // Place A far from B
        var snap0 = sim.Capture();
        Assert.True(Dist(snap0.A, snap0.B) > a.Attack.Range + 20);

        sim.SetInput(FighterSlot.A, new InputState
        {
            Attack = true,
            HasWorldAim = true,
            WorldAimX = snap0.B.X,
            WorldAimY = snap0.B.Y,
            AimX = snap0.B.X - snap0.A.X,
            AimY = snap0.B.Y - snap0.A.Y
        });

        // Cover cast time
        for (var i = 0; i < 10; i++)
            sim.Tick(0.05f);

        var snap = sim.Capture();
        Assert.True(snap.A.CdAttack > 0 || snap.Projectiles.Count > 0 || snap.A.CastingSlot == 0,
            "ranged basic attack should start even when foe is beyond exclusive melee gate");
    }

    private static float Dist(FighterState a, FighterState b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static string FindDataDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var data = Path.Combine(dir.FullName, "Data");
            if (File.Exists(Path.Combine(data, "Characters.json")))
                return dir.FullName;
            var nested = Path.Combine(dir.FullName, "ArenaDuel");
            if (File.Exists(Path.Combine(nested, "Data", "Characters.json")))
                return nested;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("ArenaDuel Data/");
    }
}
