using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MapDecals.Configuration;

/// <summary>Database connection block. Self-contained — MapDecals owns its own table/credentials.</summary>
public sealed class DatabaseConfig
{
    [JsonPropertyName("type")]     public string Type     { get; set; } = "mysql";
    [JsonPropertyName("host")]     public string Host     { get; set; } = "localhost";
    [JsonPropertyName("port")]     public int    Port     { get; set; } = 3306;
    [JsonPropertyName("database")] public string Database { get; set; } = "mapdecals";
    [JsonPropertyName("user")]     public string User     { get; set; } = "mapdecals";
    [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;
}

/// <summary>
/// A predefined decal an admin can place. Mirrors the original SwiftlyS2 plugin's
/// "Props" entries: a stable id, a display name, the material path, and a visibility permission.
/// The material is resolved at spawn time from this config — never stored in the DB.
/// </summary>
public sealed class DecalDefinition
{
    /// <summary>Stable id used to reference this decal from saved DB rows. Must be unique.</summary>
    [JsonPropertyName("uniqId")] public string UniqId { get; set; } = string.Empty;

    /// <summary>Human-friendly name shown in menus.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    /// <summary>The .vmat material path, e.g. "decals/my_decal.vmat". Precached on map load.</summary>
    [JsonPropertyName("material")] public string Material { get; set; } = string.Empty;

    /// <summary>
    /// Permission required for this decal to be VISIBLE to a player (VIP-style). Empty = visible to all.
    /// Per-player visibility is applied via the transmit manager (env_decal is a normal entity, not a pawn).
    /// </summary>
    [JsonPropertyName("showPermission")] public string ShowPermission { get; set; } = string.Empty;

    /// <summary>Default decal width (units) when first placed.</summary>
    [JsonPropertyName("width")] public float Width { get; set; } = 128f;

    /// <summary>Default decal height (units) when first placed.</summary>
    [JsonPropertyName("height")] public float Height { get; set; } = 128f;

    /// <summary>Default projection depth (units) when first placed.</summary>
    [JsonPropertyName("depth")] public int Depth { get; set; } = 12;
}

/// <summary>
/// MapDecals configuration. Mirrors the upstream SwiftlyS2 plugin's options:
/// the predefined decal list, command names/aliases, the admin permission, and a DB block.
/// </summary>
public sealed class MapDecalsConfig
{
    /// <summary>Master switch. When false the plugin loads but places/spawns nothing.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>Primary admin command (without prefix) used to open the place/edit/remove menu.</summary>
    [JsonPropertyName("command")] public string Command { get; set; } = "mapdecal";

    /// <summary>Alias commands that also open the admin menu.</summary>
    [JsonPropertyName("aliases")] public List<string> Aliases { get; set; } = ["placedecal", "placedecals", "paintmapdecal"];

    /// <summary>Permission required to place/edit/remove decals (registered via MountAdminManifest).</summary>
    [JsonPropertyName("adminPermission")] public string AdminPermission { get; set; } = "@mapdecals/admin";

    /// <summary>Predefined decals an admin may place.</summary>
    [JsonPropertyName("decals")] public List<DecalDefinition> Decals { get; set; } = [];

    /// <summary>Database connection used for the per-map decal table.</summary>
    [JsonPropertyName("database")] public DatabaseConfig Database { get; set; } = new();

    [JsonIgnore] public string FileName { get; private set; } = "mapdecals.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    public static MapDecalsConfig Load(string sharpPath, ILogger logger)
    {
        var path = Path.Combine(sharpPath, "configs", "mapdecals.json");
        try
        {
            if (!File.Exists(path))
            {
                var def = Default();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(def, JsonOpts));
                logger.LogInformation("[MapDecals] Wrote default config to {Path}", path);
                return def;
            }

            var cfg = JsonSerializer.Deserialize<MapDecalsConfig>(File.ReadAllText(path), JsonOpts);
            if (cfg is null)
            {
                logger.LogError("[MapDecals] mapdecals.json deserialized to null — using defaults");
                return Default();
            }

            return cfg;
        }
        catch (Exception e)
        {
            logger.LogError(e, "[MapDecals] Failed to load mapdecals.json — using defaults");
            return Default();
        }
    }

    private static MapDecalsConfig Default() => new()
    {
        Decals =
        [
            new DecalDefinition
            {
                UniqId         = "example_logo",
                Name           = "Example Logo",
                Material       = "decals/example_logo.vmat",
                ShowPermission = string.Empty,
                Width          = 128f,
                Height         = 128f,
                Depth          = 12,
            },
        ],
    };
}
