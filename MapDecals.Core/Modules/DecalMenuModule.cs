using System.Collections.Immutable;
using MapDecals.Configuration;
using MapDecals.Database;
using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace MapDecals.Modules;

/// <summary>
/// Registers the admin command + aliases and builds the place / edit / remove menu flow:
///   Root → Place (pick predefined → arm ping) | Edit (pick placed → resize/toggle/delete) | Reload
/// Permission is enforced via AdminManager + MountAdminManifest.
/// </summary>
internal sealed class DecalMenuModule
{
    private const string ModuleId = "MapDecals";

    private readonly InterfaceBridge          _bridge;
    private readonly MapDecalsConfig          _config;
    private readonly DecalSpawner             _spawner;
    private readonly DecalPlacementModule     _placement;
    private readonly MapDecalsDatabase        _db;
    private readonly DecalCache               _cache;
    private readonly ILogger<DecalMenuModule> _logger;

    private readonly string _permission;

    public DecalMenuModule(
        InterfaceBridge          bridge,
        MapDecalsConfig          config,
        DecalSpawner             spawner,
        DecalPlacementModule     placement,
        MapDecalsDatabase        db,
        DecalCache               cache,
        ILogger<DecalMenuModule> logger)
    {
        _bridge    = bridge;
        _config    = config;
        _spawner   = spawner;
        _placement = placement;
        _db        = db;
        _cache     = cache;
        _logger    = logger;

        // MountAdminManifest expects bare flags ("mapdecals.admin"); commands gate on "@mapdecals/admin".
        _permission = config.AdminPermission;
    }

    public void Start()
    {
        if (_bridge.MenuManager is null)
        {
            _logger.LogWarning("[MapDecals] MenuManager unavailable — !{Cmd} disabled", _config.Command);
            return;
        }

        if (_bridge.AdminManager is not { } am)
        {
            _logger.LogWarning("[MapDecals] AdminManager unavailable — admin commands NOT registered");
            return;
        }

        // Register the permission so even root '*' can resolve it (flag form, no '@' / 'group/' prefix).
        var flag = _permission.TrimStart('@');
        var group = "mapdecals";
        var bare  = flag.Contains('/') ? flag[(flag.IndexOf('/') + 1)..] : flag;

        am.MountAdminManifest(ModuleId, () => new AdminTableManifest(
            new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>
            {
                [group] = [bare],
            },
            [],
            []));

        var registry = am.GetCommandRegistry(ModuleId);
        registry.RegisterAdminCommand(_config.Command, OnCommand, ImmutableArray.Create(_permission));
        foreach (var alias in _config.Aliases)
            registry.RegisterAdminCommand(alias, OnCommand, ImmutableArray.Create(_permission));

        _logger.LogInformation("[MapDecals] !{Cmd} (+{N} aliases) registered (perm {Perm})",
            _config.Command, _config.Aliases.Count, _permission);
    }

    private void OnCommand(IGameClient? invoker, StringCommand _)
    {
        if (invoker is null || invoker.IsFakeClient)
            return;

        ShowRoot(invoker);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Root
    // ──────────────────────────────────────────────────────────────────────────

    private void ShowRoot(IGameClient admin)
    {
        if (_bridge.MenuManager is not { } mm)
            return;

        var captured = admin;
        var builder = Menu.Create().Title("Map Decals");

        builder.Item("Place Decal",  ctrl => ctrl.Next(_ => BuildPlaceMenu(captured)));
        builder.Item("Edit Decals",  ctrl => ctrl.Next(_ => BuildEditList(captured)));
        builder.Item("Reload from DB", ctrl =>
        {
            ctrl.Exit();
            ReloadFromDb(captured);
        });
        builder.ExitItem("Exit");

        mm.DisplayMenu(admin, builder.Build());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Place: pick a predefined decal, then arm the ping
    // ──────────────────────────────────────────────────────────────────────────

    private Menu BuildPlaceMenu(IGameClient admin)
    {
        var builder = Menu.Create().Title("Place Decal — Select");

        if (_config.Decals.Count == 0)
        {
            builder.DisabledItem("(no decals configured)");
        }
        else
        {
            var slot = (byte) admin.Slot.AsPrimitive();
            foreach (var def in _config.Decals)
            {
                var captured = def;
                builder.Item(def.Name, ctrl =>
                {
                    ctrl.Exit();
                    _placement.ArmPlacement(slot, captured.UniqId);
                    var live = _bridge.ClientManager.GetGameClient((PlayerSlot) slot);
                    live?.Print(HudPrintChannel.Chat,
                        $" \x04[MapDecals]\x01 Selected \x06{captured.Name}\x01 — \x09PING\x01 a surface to place it.");
                });
            }
        }

        builder.BackItem("« Back");
        builder.ExitItem("Exit");
        return builder.Build();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Edit: list placed decals → per-decal actions
    // ──────────────────────────────────────────────────────────────────────────

    private Menu BuildEditList(IGameClient admin)
    {
        var builder = Menu.Create().Title("Edit Decals");

        if (_cache.Rows.Count == 0)
        {
            builder.DisabledItem("(no decals on this map)");
        }
        else
        {
            foreach (var row in _cache.Rows)
            {
                var id    = row.Id;
                var label = $"#{row.Id} {row.DecalName} {(row.IsActive ? "[on]" : "[off]")}";
                builder.Item(label, ctrl => ctrl.Next(c => BuildDecalActions(c, id)));
            }
        }

        builder.BackItem("« Back");
        builder.ExitItem("Exit");
        return builder.Build();
    }

    private Menu BuildDecalActions(IGameClient admin, long rowId)
    {
        var row = _cache.Get(rowId);
        var builder = Menu.Create().Title(row is null ? "Decal" : $"#{row.Id} {row.DecalName}");

        if (row is null)
        {
            builder.DisabledItem("(decal no longer exists)");
            builder.BackItem("« Back");
            builder.ExitItem("Exit");
            return builder.Build();
        }

        builder.Item("Resize", ctrl => ctrl.Next(c => BuildResizeMenu(c, rowId)));

        builder.Item(row.IsActive ? "Disable" : "Enable", ctrl =>
        {
            ctrl.Exit();
            ToggleActive(admin, rowId);
        });

        builder.Item("Delete", ctrl =>
        {
            ctrl.Exit();
            DeleteDecal(admin, rowId);
        });

        builder.BackItem("« Back");
        builder.ExitItem("Exit");
        return builder.Build();
    }

    private Menu BuildResizeMenu(IGameClient admin, long rowId)
    {
        var builder = Menu.Create().Title("Resize (width x height)");

        (string label, float w, float h, int d)[] presets =
        [
            ("64 x 64",   64f,  64f,  12),
            ("128 x 128", 128f, 128f, 12),
            ("256 x 256", 256f, 256f, 16),
            ("512 x 512", 512f, 512f, 24),
        ];

        foreach (var (label, w, h, d) in presets)
        {
            var cw = w; var ch = h; var cd = d;
            builder.Item(label, ctrl =>
            {
                ctrl.Exit();
                ResizeDecal(admin, rowId, cw, ch, cd);
            });
        }

        builder.BackItem("« Back");
        builder.ExitItem("Exit");
        return builder.Build();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Edit actions — mutate cache + DB (off-thread) + live entity (game thread)
    // ──────────────────────────────────────────────────────────────────────────

    private void ToggleActive(IGameClient admin, long rowId)
    {
        var row = _cache.Get(rowId);
        if (row is null)
            return;

        row.IsActive = !row.IsActive;

        if (row.IsActive)
            _spawner.Spawn(row);
        else
            _spawner.Despawn(rowId);

        Persist(admin, row, row.IsActive ? "Enabled" : "Disabled");
    }

    private void ResizeDecal(IGameClient admin, long rowId, float width, float height, int depth)
    {
        var row = _cache.Get(rowId);
        if (row is null)
            return;

        row.Width  = width;
        row.Height = height;
        row.Depth  = depth;

        // Apply to the live entity now; full re-render guaranteed next round restart.
        _spawner.ApplyDimensions(rowId, width, height, depth);

        Persist(admin, row, $"Resized to {width:0}x{height:0}");
    }

    private void DeleteDecal(IGameClient admin, long rowId)
    {
        var row = _cache.Get(rowId);
        if (row is null)
            return;

        _spawner.Despawn(rowId);
        _cache.Remove(rowId);

        var slot = (byte) admin.Slot.AsPrimitive();
        var name = row.DecalName;
        _ = DeleteAsync(rowId, slot, name);
    }

    private async System.Threading.Tasks.Task DeleteAsync(long rowId, byte slot, string name)
    {
        var ok = await _db.DeleteAsync(rowId).ConfigureAwait(false);
        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            var c = _bridge.ClientManager.GetGameClient((PlayerSlot) slot);
            c?.Print(HudPrintChannel.Chat, ok
                ? $" \x04[MapDecals]\x01 Deleted \x06{name}\x01 (#{rowId})."
                : " \x02[MapDecals]\x01 Delete failed in DB.");
        });
    }

    private void Persist(IGameClient admin, MapDecalRow row, string verb)
    {
        var slot = (byte) admin.Slot.AsPrimitive();
        var name = row.DecalName;
        var id   = row.Id;
        _ = PersistAsync(row, slot, $"{verb} {name} (#{id})");
    }

    private async System.Threading.Tasks.Task PersistAsync(MapDecalRow row, byte slot, string message)
    {
        var ok = await _db.UpdateAsync(row).ConfigureAwait(false);
        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            var c = _bridge.ClientManager.GetGameClient((PlayerSlot) slot);
            c?.Print(HudPrintChannel.Chat, ok
                ? $" \x04[MapDecals]\x01 {message}."
                : " \x02[MapDecals]\x01 DB update failed.");
        });
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Reload
    // ──────────────────────────────────────────────────────────────────────────

    private void ReloadFromDb(IGameClient admin)
    {
        var slot = (byte) admin.Slot.AsPrimitive();
        var map  = _bridge.ModSharp.GetMapName() ?? string.Empty;
        _ = ReloadAsync(map, slot);
    }

    private async System.Threading.Tasks.Task ReloadAsync(string map, byte slot)
    {
        var rows = await _db.GetByMapAsync(map).ConfigureAwait(false);
        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            _spawner.DespawnAll();
            _cache.Replace(rows);
            _spawner.RespawnAll(_cache.Rows);

            var c = _bridge.ClientManager.GetGameClient((PlayerSlot) slot);
            c?.Print(HudPrintChannel.Chat, $" \x04[MapDecals]\x01 Reloaded {rows.Count} decal(s) for {map}.");
        });
    }
}
