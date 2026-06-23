using System.Collections.Generic;
using MapDecals.Database;

namespace MapDecals.Modules;

/// <summary>
/// Holds the current map's loaded decal rows, shared across modules (placement adds, menus
/// list/edit/remove, spawner reads on round start). Mutated only on the game thread.
/// </summary>
internal sealed class DecalCache
{
    private readonly List<MapDecalRow> _rows = [];

    public IReadOnlyList<MapDecalRow> Rows => _rows;

    public void Replace(IEnumerable<MapDecalRow> rows)
    {
        _rows.Clear();
        _rows.AddRange(rows);
    }

    public void Add(MapDecalRow row) => _rows.Add(row);

    public void Remove(long id) => _rows.RemoveAll(r => r.Id == id);

    public MapDecalRow? Get(long id)
    {
        foreach (var r in _rows)
            if (r.Id == id)
                return r;

        return null;
    }

    public void Clear() => _rows.Clear();
}
