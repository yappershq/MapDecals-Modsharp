using System;
using System.Collections.Generic;
using System.Globalization;
using MapDecals.Configuration;
using MapDecals.Database;
using Microsoft.Extensions.Logging;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace MapDecals.Modules;

/// <summary>
/// Spawns and tracks <c>env_decal</c> entities for the current map. env_decal entities are cleared
/// on every round restart, so the active set is re-spawned on each round (re)start. Spawned entity
/// references are tracked by <see cref="CEntityHandle{T}"/> (RefHandle) — never raw pointers, which
/// die on round end.
///
/// All entity operations here MUST run on the game thread.
/// </summary>
internal sealed class DecalSpawner
{
    private readonly InterfaceBridge          _bridge;
    private readonly MapDecalsConfig          _config;
    private readonly ILogger<DecalSpawner>    _logger;

    /// <summary>Tracks live entity handles per DB row id so edits/removals can find the spawned entity.</summary>
    private readonly Dictionary<long, CEntityHandle<IBaseEntity>> _spawned = [];

    public DecalSpawner(InterfaceBridge bridge, MapDecalsConfig config, ILogger<DecalSpawner> logger)
    {
        _bridge = bridge;
        _config = config;
        _logger = logger;
    }

    /// <summary>Precache every configured material so the decal material strong-handle resolves at spawn.</summary>
    public void PrecacheAll()
    {
        foreach (var def in _config.Decals)
        {
            if (string.IsNullOrWhiteSpace(def.Material))
                continue;

            _bridge.ModSharp.PrecacheResource(def.Material);
            _logger.LogInformation("[MapDecals] Precached material {Material}", def.Material);
        }
    }

    /// <summary>
    /// Re-spawn the full active set for the current round. Clears any stale tracking first. Game thread only.
    /// </summary>
    public void RespawnAll(IReadOnlyList<MapDecalRow> rows)
    {
        _spawned.Clear();

        foreach (var row in rows)
        {
            if (!row.IsActive)
                continue;

            Spawn(row);
        }

        _logger.LogInformation("[MapDecals] Spawned {Count} active decal(s) for {Map}",
            _spawned.Count, _bridge.ModSharp.GetMapName());
    }

    /// <summary>Spawn a single decal row and track its handle. Returns the entity (or null on failure). Game thread only.</summary>
    public IBaseEntity? Spawn(MapDecalRow row)
    {
        var def = FindDefinition(row.DecalId);
        if (def is null)
        {
            _logger.LogWarning("[MapDecals] No config decal for id '{Id}' — skipping row {Row}", row.DecalId, row.Id);
            return null;
        }

        if (string.IsNullOrWhiteSpace(def.Material))
        {
            _logger.LogWarning("[MapDecals] Decal '{Id}' has no material — skipping", def.UniqId);
            return null;
        }

        if (!TryParseVector(row.Position, out var origin))
        {
            _logger.LogWarning("[MapDecals] Bad position '{Pos}' on row {Row}", row.Position, row.Id);
            return null;
        }

        TryParseVector(row.Angles, out var angles); // angles default to (0,0,0) when unparseable

        // env_decal does not require the precache pipeline of SpawnEntitySync (we precache materials
        // ourselves in OnResourcePrecache), so the fast create + DispatchSpawn path is correct here.
        var ent = _bridge.EntityManager.CreateEntityByName<IBaseEntity>("env_decal");
        if (ent is null)
        {
            _logger.LogError("[MapDecals] CreateEntityByName('env_decal') returned null");
            return null;
        }

        // Keyvalue keys MUST be lowercase — the engine will not recognize them otherwise.
        var kv = new Dictionary<string, KeyValuesVariantValueItem>
        {
            ["material"]       = def.Material,
            ["width"]          = row.Width,
            ["height"]         = row.Height,
            ["depth"]          = (float) row.Depth,
            ["renderorder"]    = 1,
            ["projectonworld"] = true,
        };

        ent.SetName($"md_decal_{row.Id}_{_bridge.ModSharp.GetGlobals().TickCount}");
        ent.DispatchSpawn(kv);
        ent.Teleport(origin, angles, null);

        // Belt-and-braces: push dimensions as netvars too — the "OnDecalDimensionsChanged" callback
        // needs them set for correct rendering, and re-applies on edits.
        ent.SetNetVar("m_flWidth",         row.Width);
        ent.SetNetVar("m_flHeight",        row.Height);
        ent.SetNetVar("m_flDepth",         (float) row.Depth);
        ent.SetNetVar("m_nRenderOrder",    1u);
        ent.SetNetVar("m_bProjectOnWorld", true);

        _spawned[row.Id] = ent.RefHandle;

        // VIP visibility: env_decal is a normal entity (not a pawn), so per-player transmit works.
        if (row.ForceOnVip && !string.IsNullOrEmpty(def.ShowPermission))
            ApplyVipVisibility(ent, def.ShowPermission);

        return ent;
    }

    /// <summary>Move a spawned entity's tracking from a temporary key to its real DB id. Game thread only.</summary>
    public void Retrack(long fromId, long toId)
    {
        if (fromId == toId)
            return;

        if (_spawned.Remove(fromId, out var handle))
            _spawned[toId] = handle;
    }

    /// <summary>Resolve the live entity for a tracked row, or null if it's gone. Game thread only.</summary>
    public IBaseEntity? Resolve(long rowId)
    {
        if (!_spawned.TryGetValue(rowId, out var handle))
            return null;

        var ent = _bridge.EntityManager.FindEntityByHandle(handle);
        if (ent is null || !ent.IsValid())
        {
            _spawned.Remove(rowId);
            return null;
        }

        return ent;
    }

    /// <summary>Kill the live entity for a row (if any) and drop tracking. Game thread only.</summary>
    public void Despawn(long rowId)
    {
        var ent = Resolve(rowId);
        ent?.Kill();
        _spawned.Remove(rowId);
    }

    /// <summary>Kill every tracked decal and clear tracking (e.g. on map change / shutdown). Game thread only.</summary>
    public void DespawnAll()
    {
        foreach (var handle in _spawned.Values)
        {
            var ent = _bridge.EntityManager.FindEntityByHandle(handle);
            if (ent is not null && ent.IsValid())
                ent.Kill();
        }

        _spawned.Clear();
    }

    /// <summary>Push updated dimensions onto a live decal entity (used by the resize edit flow). Game thread only.</summary>
    public void ApplyDimensions(long rowId, float width, float height, int depth)
    {
        var ent = Resolve(rowId);
        if (ent is null)
            return;

        ent.SetNetVar("m_flWidth",  width);
        ent.SetNetVar("m_flHeight", height);
        ent.SetNetVar("m_flDepth",  (float) depth);
    }

    public DecalDefinition? FindDefinition(string uniqId)
    {
        foreach (var def in _config.Decals)
        {
            if (string.Equals(def.UniqId, uniqId, StringComparison.OrdinalIgnoreCase))
                return def;
        }

        return null;
    }

    // ──────────────────────────────────────────────────────────────────────────

    private void ApplyVipVisibility(IBaseEntity ent, string permission)
    {
        var tm = _bridge.TransmitManager;

        // default-transmit = false: hidden for everyone, then opened up per permitted controller.
        // AddEntityHooks hard-rejects player pawns (returns false) — env_decal is fine, but always
        // check the return so a failed hook doesn't silently leave the decal visible to everyone.
        if (!tm.IsEntityHooked(ent) && !tm.AddEntityHooks(ent, defaultTransmit: false))
        {
            _logger.LogWarning("[MapDecals] Failed to hook decal {Index} for VIP visibility — skipping", ent.Index);
            return;
        }

        foreach (var client in _bridge.ClientManager.GetGameClients())
        {
            if (client.IsFakeClient || client.IsHltv)
                continue;

            var admin   = _bridge.AdminManager?.GetAdmin(client.SteamId);
            var allowed = admin?.HasPermission(permission) ?? false;

            tm.SetEntityState(ent.Index, client.ControllerIndex, allowed, channel: 0);
        }
    }

    private static bool TryParseVector(string raw, out Vector vec)
    {
        vec = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
            return false;

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return false;

        vec = new Vector(x, y, z);
        return true;
    }

    public static string FormatVector(Vector v) =>
        string.Create(CultureInfo.InvariantCulture, $"{v.X} {v.Y} {v.Z}");
}
