using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArenaDuel.Data;

public sealed class CharacterCatalog
{
    [JsonPropertyName("characters")]
    public List<CharacterDef> Characters { get; set; } = [];
}

public sealed class ArenaCatalog
{
    [JsonPropertyName("arenas")]
    public List<ArenaDef> Arenas { get; set; } = [];
}

public sealed class CharacterDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Color { get; set; } = "#FFFFFF";
    public float MaxHp { get; set; }
    public float MaxMp { get; set; }
    public float MoveSpeed { get; set; }
    public float Radius { get; set; } = 15;
    public float MpRegen { get; set; } = 4;
    public SkillDef Attack { get; set; } = new();
    public SkillDef Skill1 { get; set; } = new();
    public SkillDef Skill2 { get; set; } = new();
    public SkillDef Ultimate { get; set; } = new();
}

public sealed class SkillDef
{
    public string Name { get; set; } = "";
    public float Damage { get; set; }
    public float Cooldown { get; set; }
    public float ManaCost { get; set; }
    public float Range { get; set; }
    public string Type { get; set; } = "projectile";
    public float Speed { get; set; }
    public float Radius { get; set; } = 8;
    public float Lifetime { get; set; } = 0.5f;
    public float CastTime { get; set; }
    public float Shield { get; set; }
    public float SlowFactor { get; set; }
    public float SlowDuration { get; set; }
    public float Knockback { get; set; }
    public float LifeSteal { get; set; }
    public float AntiHealDuration { get; set; }
    public float DotDamage { get; set; }
    public float DotDuration { get; set; }
    public float SilenceDuration { get; set; }
    public float RootDuration { get; set; }
    public float MarkDuration { get; set; }
    public float MarkBonus { get; set; }
    public float ChainBonus { get; set; }
    public int Count { get; set; } = 1;
    public float Spread { get; set; }
}

public sealed class ArenaDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public float Width { get; set; }
    public float Height { get; set; }
    public string Background { get; set; } = "#111";
    public string Accent { get; set; } = "#888";
    public string Floor { get; set; } = "#0A0A0A";
    public string Theme { get; set; } = "plain";
    public string OutOfBounds { get; set; } = "clamp";
    public float OutDamagePerSecond { get; set; }
    public RectDef? Bridge { get; set; }
    public List<RectDef> Obstacles { get; set; } = [];
    public List<PropDef> Props { get; set; } = [];
    public VecDef SpawnA { get; set; } = new();
    public VecDef SpawnB { get; set; } = new();
}

public sealed class PropDef
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
    public string Kind { get; set; } = "deco";
}

public sealed class RectDef
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
}

public sealed class VecDef
{
    public float X { get; set; }
    public float Y { get; set; }
}

public static class GameData
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static CharacterCatalog Characters { get; private set; } = new();
    public static ArenaCatalog Arenas { get; private set; } = new();

    public static void Load(string baseDir)
    {
        var charPath = Path.Combine(baseDir, "Data", "Characters.json");
        var arenaPath = Path.Combine(baseDir, "Data", "Arenas.json");
        Characters = JsonSerializer.Deserialize<CharacterCatalog>(File.ReadAllText(charPath), JsonOptions)
                     ?? new CharacterCatalog();
        Arenas = JsonSerializer.Deserialize<ArenaCatalog>(File.ReadAllText(arenaPath), JsonOptions)
                 ?? new ArenaCatalog();
    }

    public static CharacterDef GetCharacter(string id) =>
        Characters.Characters.First(c => c.Id == id);

    public static ArenaDef GetArena(string id) =>
        Arenas.Arenas.First(a => a.Id == id);
}
