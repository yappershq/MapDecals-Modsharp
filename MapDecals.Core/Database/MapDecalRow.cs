using SqlSugar;

namespace MapDecals.Database;

/// <summary>
/// A persisted decal placement, keyed by map. The material itself is NOT stored — only the
/// config <see cref="DecalId"/> reference, resolved against <see cref="Configuration.DecalDefinition"/>
/// at spawn time (matches upstream). Position/Angles are stored as "x y z" / "p y r" strings.
///
/// NOTE: <see cref="Id"/> is <c>long</c> (never <c>ulong</c> — SqlSugar/BSON-style boundary gotcha).
/// </summary>
[SugarTable("cc_mapdecals")]
internal sealed class MapDecalRow
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(Length = 64, IndexGroupNameList = ["idx_map"])]
    public string Map { get; set; } = string.Empty;

    /// <summary>References <see cref="Configuration.DecalDefinition.UniqId"/>.</summary>
    [SugarColumn(Length = 64)]
    public string DecalId { get; set; } = string.Empty;

    /// <summary>Cached display name (snapshot at placement time; menus prefer the live config name).</summary>
    [SugarColumn(Length = 64)]
    public string DecalName { get; set; } = string.Empty;

    /// <summary>World origin as "x y z".</summary>
    [SugarColumn(Length = 64)]
    public string Position { get; set; } = string.Empty;

    /// <summary>Angles as "pitch yaw roll".</summary>
    [SugarColumn(Length = 64)]
    public string Angles { get; set; } = string.Empty;

    public int   Depth  { get; set; } = 12;
    public float Width  { get; set; } = 128f;
    public float Height { get; set; } = 128f;

    /// <summary>If true, the decal is only transmitted to players holding the definition's ShowPermission.</summary>
    public bool ForceOnVip { get; set; }

    /// <summary>Soft toggle — inactive rows are kept in the DB but not spawned.</summary>
    public bool IsActive { get; set; } = true;
}
