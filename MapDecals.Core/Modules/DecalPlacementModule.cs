using System.Collections.Concurrent;
using MapDecals.Configuration;
using MapDecals.Database;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace MapDecals.Modules;

/// <summary>
/// Placement UX (matches upstream): an admin selects a decal in the menu, then PINGs a surface.
/// The ping's world coordinates become the decal origin. This listener catches <c>player_ping</c>
/// and, if the pinging admin has a pending selection, spawns + persists the decal there.
/// </summary>
internal sealed class DecalPlacementModule : IEventListener, IClientListener
{
    private readonly InterfaceBridge              _bridge;
    private readonly MapDecalsConfig              _config;
    private readonly DecalSpawner                 _spawner;
    private readonly MapDecalsDatabase            _db;
    private readonly DecalCache                   _cache;
    private readonly ILogger<DecalPlacementModule> _logger;

    /// <summary>slot → pending decal definition uniqId awaiting a ping. Thread-safe (set from menu thread).</summary>
    private readonly ConcurrentDictionary<byte, string> _pending = new();

    /// <summary>Decrementing key for tracking a freshly-spawned decal before its DB id is known.</summary>
    private long _tempIdSeq = -1;

    // IEventListener / IClientListener share these two members.
    public int ListenerPriority => 0;
    public int ListenerVersion  => IEventListener.ApiVersion;

    public DecalPlacementModule(
        InterfaceBridge               bridge,
        MapDecalsConfig               config,
        DecalSpawner                  spawner,
        MapDecalsDatabase             db,
        DecalCache                    cache,
        ILogger<DecalPlacementModule> logger)
    {
        _bridge  = bridge;
        _config  = config;
        _spawner = spawner;
        _db      = db;
        _cache   = cache;
        _logger  = logger;
    }

    public void Start()
    {
        _bridge.EventManager.InstallEventListener(this);
        _bridge.EventManager.HookEvent("player_ping");
        _bridge.ClientManager.InstallClientListener(this);
    }

    public void Stop()
    {
        _bridge.ClientManager.RemoveClientListener(this);
        _bridge.EventManager.RemoveEventListener(this);
        _pending.Clear();
    }

    /// <summary>Arm placement: next ping by <paramref name="slot"/> places <paramref name="decalUniqId"/>.</summary>
    public void ArmPlacement(byte slot, string decalUniqId) => _pending[slot] = decalUniqId;

    /// <summary>Cancel a pending placement (e.g. admin closed the menu).</summary>
    public void Cancel(byte slot) => _pending.TryRemove(slot, out _);

    // IEventListener — fires on the game thread.
    public void FireGameEvent(IGameEvent @event)
    {
        if (@event.Name != "player_ping")
            return;

        var controller = @event.GetPlayerController("userid");
        if (controller is null)
            return;

        var slot = (byte) controller.PlayerSlot.AsPrimitive();
        if (!_pending.TryRemove(slot, out var decalUniqId))
            return; // this admin has no pending placement — ignore the ping

        var def = _spawner.FindDefinition(decalUniqId);
        if (def is null)
            return;

        var origin = new Vector(@event.GetFloat("x"), @event.GetFloat("y"), @event.GetFloat("z"));

        var client = _bridge.ClientManager.GetGameClient(controller.PlayerSlot);
        var map    = _bridge.ModSharp.GetMapName() ?? string.Empty;

        // Track this spawn under a unique temporary negative id until the DB hands back the real id.
        var tempId = System.Threading.Interlocked.Decrement(ref _tempIdSeq) + 1;

        var row = new MapDecalRow
        {
            Id         = tempId,
            Map        = map,
            DecalId    = def.UniqId,
            DecalName  = def.Name,
            Position   = DecalSpawner.FormatVector(origin),
            Angles     = "0 0 0",
            Width      = def.Width,
            Height     = def.Height,
            Depth      = def.Depth,
            ForceOnVip = !string.IsNullOrEmpty(def.ShowPermission),
            IsActive   = true,
        };

        // Spawn live immediately on this (game) thread so the admin sees it at once.
        var ent = _spawner.Spawn(row);
        if (ent is null)
        {
            client?.Print(HudPrintChannel.Chat, " \x02[MapDecals]\x01 Failed to spawn the decal.");
            return;
        }

        client?.Print(HudPrintChannel.Chat, $" \x04[MapDecals]\x01 Placed \x06{def.Name}\x01. Saving...");

        // Persist off-thread; backfill the row id, re-key the live spawn tracking and add to the cache.
        _ = PersistAsync(row, tempId, slot, def.Name);
    }

    private async System.Threading.Tasks.Task PersistAsync(MapDecalRow row, long tempId, byte slot, string decalName)
    {
        row.Id = 0; // let the DB assign the identity
        var ok = await _db.InsertAsync(row).ConfigureAwait(false);

        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            var c = _bridge.ClientManager.GetGameClient((PlayerSlot) slot);

            if (!ok)
            {
                _spawner.Despawn(tempId);
                c?.Print(HudPrintChannel.Chat, " \x02[MapDecals]\x01 Save failed — decal removed.");
                return;
            }

            // Move live-spawn tracking from the temp key to the real DB id, and cache the row.
            _spawner.Retrack(tempId, row.Id);
            _cache.Add(row);

            c?.Print(HudPrintChannel.Chat, $" \x04[MapDecals]\x01 Saved \x06{decalName}\x01 (#{row.Id}).");
            _logger.LogInformation("[MapDecals] Saved decal #{Id} '{Name}' on {Map}", row.Id, decalName, row.Map);
        });
    }

    // ──────────────────────────────────────────────────────────────────────────
    // IClientListener — drop per-slot pending placement so a reused slot can't
    // inherit a disconnected admin's armed decal.
    // ──────────────────────────────────────────────────────────────────────────

    public void OnClientDisconnecting(IGameClient client, NetworkDisconnectionReason reason)
        => _pending.TryRemove((byte) client.Slot.AsPrimitive(), out _);

    public void OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason) { }
    public void OnClientConnected(IGameClient client) { }
    public void OnClientPutInServer(IGameClient client) { }
    public void OnClientPostAdminCheck(IGameClient client) { }
    public void OnClientSettingChanged(IGameClient client) { }
    public void OnAdminCacheReload() { }
}
