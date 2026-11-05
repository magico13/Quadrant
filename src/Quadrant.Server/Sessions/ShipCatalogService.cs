using System.Text.Json;
using Quadrant.RulesEngine;

namespace Quadrant.Server.Sessions;

/// <summary>A ship selection made during setup: which catalog ship and which team.</summary>
public record ShipSelection(string ShipId, int TeamId);

/// <summary>
/// Loads weapon definitions from weapons/ and ship definitions from ships/ JSON folders.
/// Ships reference weapons by ID; this service resolves them at load time.
/// </summary>
public class ShipCatalogService
{
    private readonly List<ShipDefinition> _ships = new();
    private readonly Dictionary<string, WeaponDef> _weapons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public IReadOnlyList<ShipDefinition> All => _ships;

    /// <summary>Load all weapon JSON files from the given directory.</summary>
    public void LoadWeapons(string directory)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var w = JsonSerializer.Deserialize<WeaponDef>(json, JsonOpts);
                if (w is not null && !string.IsNullOrEmpty(w.Id))
                    _weapons[w.Id] = w;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ShipCatalog] Failed to load weapon {file}: {ex.Message}");
            }
        }

        Console.WriteLine($"[ShipCatalog] Loaded {_weapons.Count} weapons.");
    }

    /// <summary>Load all ship JSON files from the given directory, resolving weapon references.</summary>
    public void LoadShips(string directory)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var entry = JsonSerializer.Deserialize<ShipJson>(json, JsonOpts);
                if (entry is null) continue;

                var def = ToDefinition(entry);
                _ships.Add(def);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ShipCatalog] Failed to load ship {file}: {ex.Message}");
            }
        }

        _ships.Sort((a, b) =>
        {
            int family = string.Compare(a.Family, b.Family, StringComparison.OrdinalIgnoreCase);
            if (family != 0) return family;
            int tier = a.Tier.CompareTo(b.Tier);
            return tier != 0 ? tier : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        Console.WriteLine($"[ShipCatalog] Loaded {_ships.Count} ships.");
    }

    /// <summary>Look up a ship by its catalog ID.</summary>
    public ShipDefinition? Get(string id) =>
        _ships.FirstOrDefault(s => s.Id == id);

    /// <summary>Spread team leaders around the first leader; place allies near allies.</summary>
    public List<ShipInstance> CreateShips(List<ShipSelection> selections, Random? random = null)
    {
        random ??= Random.Shared;
        if (selections.Any(s => s.TeamId < 0 || Get(s.ShipId) is null))
            throw new ArgumentException("Every selection must have a valid ship and non-negative team ID.");
        if (selections.Select(s => s.TeamId).Distinct().Count() < 2)
            throw new ArgumentException("At least two teams are required.");

        var ships = new List<ShipInstance>();
        var occupied = new HashSet<Hex>();
        var leaders = new List<Hex>();
        var teams = selections.GroupBy(s => s.TeamId).ToList();
        // The two-side rule sets a distance between leaders. For more teams,
        // keep each new leader at its rolled distance from the first and favor
        // similar separation from every other leader (a triangle for 3 teams).
        foreach (var team in teams)
        {
            Hex leader = new(0, 0);
            if (leaders.Count > 0)
            {
                int distance = random.Next(1, 7) + random.Next(1, 7) + random.Next(1, 7) + 5;
                var first = leaders[0];
                var ring = HexesWithin(first, distance).Where(h => h.DistanceTo(first) == distance && !occupied.Contains(h)).ToList();
                while (ring.Count == 0)
                {
                    distance++;
                    ring = HexesWithin(first, distance).Where(h => h.DistanceTo(first) == distance && !occupied.Contains(h)).ToList();
                }
                var safe = ring.Where(h => leaders.All(other => h.DistanceTo(other) >= 8)).ToList();
                if (safe.Count > 0) ring = safe;
                int best = ring.Min(h => leaders.Sum(other => Math.Abs(h.DistanceTo(other) - distance)));
                var choices = ring.Where(h => leaders.Sum(other => Math.Abs(h.DistanceTo(other) - distance)) == best).ToList();
                leader = choices[random.Next(choices.Count)];
            }
            leaders.Add(leader);
            occupied.Add(leader);
        }

        int counter = 1;
        for (int teamIndex = 0; teamIndex < teams.Count; teamIndex++)
        {
            var team = teams[teamIndex];
            var definitions = team.Select(s => Get(s.ShipId)!).OrderByDescending(s => s.Tier).ToList();
            var leader = leaders[teamIndex];
            var facing = (Facing)random.Next(6);
            var allies = new List<Hex>();
            foreach (var definition in definitions)
            {
                var position = leader;
                if (allies.Count > 0)
                {
                    var choices = allies.SelectMany(h => HexesWithin(h, 3)).Where(h => !occupied.Contains(h)).Distinct().ToList();
                    var safe = choices.Where(h => ships.Where(s => s.TeamId != team.Key).All(s => h.DistanceTo(s.Position) >= 4)
                        && leaders.Where((_, i) => i != teamIndex).All(other => h.DistanceTo(other) >= 4)).ToList();
                    if (safe.Count > 0) choices = safe;
                    position = choices[random.Next(choices.Count)];
                }
                occupied.Add(position);
                allies.Add(position);
                ships.Add(definition.Instantiate($"ship-{counter++}", position, facing, team.Key));
            }
        }
        return ships;
    }

    private static IEnumerable<Hex> HexesWithin(Hex center, int radius)
    {
        for (int q = -radius; q <= radius; q++)
            for (int r = Math.Max(-radius, -q - radius); r <= Math.Min(radius, -q + radius); r++)
                yield return new Hex(center.Q + q, center.R + r);
    }

    /// <summary>Default fleet uses the same placement rules as selected fleets.</summary>
    public List<ShipInstance> CreateDefaultShips() => CreateShips([
        new("deaths-curtain-necrofighter", 0), new("barrow-boneshard", 0), new("grave-casket", 1)
    ]);

    private ShipDefinition ToDefinition(ShipJson entry)
    {
        var weapons = new List<Weapon>();
        foreach (var wRef in entry.Weapons ?? [])
        {
            if (_weapons.TryGetValue(wRef.WeaponId, out var wDef))
            {
                weapons.Add(new Weapon
                {
                    Name = wDef.Name,
                    Arc = ParseArc(wRef.Arc),
                    LinkedGroup = wRef.LinkedGroup,
                    BroadArc = wDef.Special.Any(s => string.Equals(s, "Broad Arc", StringComparison.OrdinalIgnoreCase)),
                    PointDefenseBonus = ParsePointDefenseBonus(wDef.Special),
                    MinRange = wDef.MinRange,
                    RangeIncrement = wDef.RangeIncrement,
                    MaxRange = wDef.MaxRange,
                    Damage = DamageExpression.From(wRef.Damage ?? wDef.Damage),
                    Type = wDef.Type == "Tracking" ? WeaponType.Tracking : WeaponType.DirectFire,
                    ProjectileSpeed = wDef.Speed,
                    LimitedFireUses = ParseLimitedFireUses(wDef.Special)
                });
            }
            else
            {
                Console.Error.WriteLine($"[ShipCatalog] Ship '{entry.Id}' references unknown weapon '{wRef.WeaponId}'");
            }
        }

        return new ShipDefinition
        {
            Id = entry.Id,
            Name = entry.Name,
            Family = string.IsNullOrWhiteSpace(entry.Family) ? "Other" : entry.Family.Trim(),
            Tier = entry.Tier,
            Size = entry.Type?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
            Speed = entry.Speed,
            Maneuverability = ParseManeuverability(entry.Maneuverability),
            HullPoints = entry.Hp,
            DamageThreshold = entry.Dt ?? 0,
            CriticalThreshold = entry.Ct,
            BaseAC = entry.Ac,
            BaseTL = entry.Tl,
            PowerCorePcu = entry.PowerCore?.Pcu ?? 0,
            Countermeasures = entry.Countermeasures > 0 ? entry.Countermeasures : ParseSystemRank(entry.Systems, "defenses"),
            ComputersModifier = entry.Modifiers?.Computers ?? 0,
            HasSensors = (entry.Systems ?? []).Any(s => s.Contains("sensor", StringComparison.OrdinalIgnoreCase)),
            SensorRangeHexes = ParseSensorRange(entry.Systems),
            Complement = entry.Complement,
            Crew = (entry.Crew ?? []).Select(c => new CrewDefinition(c.Role, c.Skills ?? new Dictionary<string, int>(),
                c.Ranks ?? Math.Max(1, (int)entry.Tier))).ToList(),
            Shields = new QuadrantShields
            {
                Forward = entry.Shields?.Forward ?? 0,
                Port = entry.Shields?.Port ?? 0,
                Starboard = entry.Shields?.Starboard ?? 0,
                Aft = entry.Shields?.Aft ?? 0
            },
            Weapons = weapons
        };
    }

    private static int ParseLimitedFireUses(IEnumerable<string> specialProperties)
    {
        var tag = specialProperties.FirstOrDefault(s => s.StartsWith("Limited Fire", StringComparison.OrdinalIgnoreCase));
        if (tag is null) return 0;
        var digits = new string(tag.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int uses) ? uses : 0;
    }

    private static int ParsePointDefenseBonus(IEnumerable<string> specialProperties)
    {
        var tag = specialProperties.FirstOrDefault(s => s.StartsWith("Point", StringComparison.OrdinalIgnoreCase));
        if (tag is null) return 0;
        var match = System.Text.RegularExpressions.Regex.Match(tag, @"\bPoint\s*\(\s*\+?(\d+)\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out int bonus) ? bonus : 0;
    }

    private static int ParseSystemRank(IEnumerable<string>? systems, string system)
    {
        var entry = systems?.FirstOrDefault(s => s.Contains(system, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return 0;
        var match = System.Text.RegularExpressions.Regex.Match(entry, @"\bmk\s*(\d+)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out int rank) ? rank : 0;
    }

    private static int ParseSensorRange(IEnumerable<string>? systems)
    {
        var sensor = systems?.FirstOrDefault(s => s.Contains("sensor", StringComparison.OrdinalIgnoreCase));
        if (sensor is null) return 0;
        if (sensor.Contains("short-range", StringComparison.OrdinalIgnoreCase)) return 5;
        if (sensor.Contains("medium-range", StringComparison.OrdinalIgnoreCase) || sensor.Contains("mid-range", StringComparison.OrdinalIgnoreCase)) return 10;
        if (sensor.Contains("long-range", StringComparison.OrdinalIgnoreCase)) return 20;
        return 0; // Some special sensor systems do not list a hex range.
    }

    private static FiringArc ParseArc(string arc) => arc.ToLowerInvariant() switch
    {
        "forward" => FiringArc.Forward,
        "starboard" => FiringArc.Starboard,
        "aft" => FiringArc.Aft,
        "port" => FiringArc.Port,
        "allarcs" or "all" => FiringArc.AllArcs,
        _ => FiringArc.Forward
    };

    private static Maneuverability ParseManeuverability(string m) => m.ToLowerInvariant() switch
    {
        "clumsy" => Maneuverability.Clumsy,
        "poor" => Maneuverability.Poor,
        "average" => Maneuverability.Average,
        "good" => Maneuverability.Good,
        "perfect" => Maneuverability.Perfect,
        _ => Maneuverability.Average
    };
}

// --- JSON deserialization models ---

public class ShipJson
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string? Family { get; set; }
    public double Tier { get; set; } = 1;
    public string? Source { get; set; }
    public string? Type { get; set; }
    public int Speed { get; set; }
    public string Maneuverability { get; set; } = "average";
    public int Ac { get; set; }
    public int Tl { get; set; }
    public int Hp { get; set; }
    public int? Dt { get; set; }
    public int Ct { get; set; }
    public int Countermeasures { get; set; }
    public ShipSystemsModifiersJson? Modifiers { get; set; }
    public List<string>? Systems { get; set; }
    public ShipShieldsJson? Shields { get; set; }
    public List<ShipWeaponRef>? Weapons { get; set; }
    public ShipPowerCoreJson? PowerCore { get; set; }
    public int? Complement { get; set; }
    public List<ShipCrewJson>? Crew { get; set; }
}

public class ShipSystemsModifiersJson
{
    public int Computers { get; set; }
    public int Piloting { get; set; }
}

public class ShipPowerCoreJson
{
    public int Pcu { get; set; }
}

public class ShipCrewJson
{
    public required string Role { get; set; }
    public Dictionary<string, int>? Skills { get; set; }
    public int? Ranks { get; set; }
}

public class ShipShieldsJson
{
    public string? Type { get; set; }
    public int Total { get; set; }
    public int Forward { get; set; }
    public int Port { get; set; }
    public int Starboard { get; set; }
    public int Aft { get; set; }
}

public class ShipWeaponRef
{
    public required string WeaponId { get; set; }
    public string Arc { get; set; } = "forward";
    public string? LinkedGroup { get; set; }
    public string? Damage { get; set; }
}

public class WeaponDef
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string Type { get; set; } = "Direct-Fire";
    public int MinRange { get; set; } = 1;
    public int RangeIncrement { get; set; } = 5;
    public int MaxRange { get; set; }
    public int Speed { get; set; }
    public required string Damage { get; set; }
    public int Pcu { get; set; }
    public int Cost { get; set; }
    public List<string> Special { get; set; } = new();
}
