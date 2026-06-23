using MapDecals.Configuration;
using MapDecals.Database;
using MapDecals.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Listeners;

namespace MapDecals;

/// <summary>
/// MapDecals — place persistent decals on map geometry by pinging a surface.
///
/// Port of Cruze03/CS2-MapDecals-SwiftlyS2 to ModSharp. Spawns <c>env_decal</c> entities for
/// per-map saved placements; admins select a predefined decal in a menu then ping a wall to place it.
///
/// Lifecycle (established ModSharp rules):
///   - Init:               install game listener + event listener, connect DB.
///   - OnAllModulesLoaded:  resolve AdminManager/MenuManager, mount manifest, register commands.
///   - OnResourcePrecache:  precache every configured material.
///   - OnServerActivate:    load this map's decals from DB (async, off thread).
///   - OnRoundRestarted:    re-spawn the active set (env_decal is wiped each round).
/// </summary>
public sealed class MapDecalsPlugin : IModSharpModule, IGameListener
{
    public string DisplayName   => "MapDecals";
    public string DisplayAuthor => "yappershq";

    private readonly ILogger<MapDecalsPlugin> _logger;
    private readonly InterfaceBridge          _bridge;
    private readonly MapDecalsConfig          _config;
    private readonly MapDecalsDatabase        _db;
    private readonly DecalCache               _cache;
    private readonly DecalSpawner             _spawner;
    private readonly DecalPlacementModule     _placement;
    private readonly DecalMenuModule          _menu;

    public int ListenerPriority => 0;
    public int ListenerVersion  => IGameListener.ApiVersion;

    public MapDecalsPlugin(
        ISharedSystem  sharedSystem,
        string         dllPath,
        string         sharpPath,
        System.Version version,
        IConfiguration configuration,
        bool           hotReload)
    {
        var loggerFactory = sharedSystem.GetLoggerFactory();
        _logger = loggerFactory.CreateLogger<MapDecalsPlugin>();

        _bridge    = new InterfaceBridge(sharpPath, sharedSystem);
        _config    = MapDecalsConfig.Load(sharpPath, loggerFactory.CreateLogger<MapDecalsConfig>());
        _db        = new MapDecalsDatabase(loggerFactory.CreateLogger<MapDecalsDatabase>());
        _cache     = new DecalCache();
        _spawner   = new DecalSpawner(_bridge, _config, loggerFactory.CreateLogger<DecalSpawner>());
        _placement = new DecalPlacementModule(_bridge, _config, _spawner, _db, _cache,
            loggerFactory.CreateLogger<DecalPlacementModule>());
        _menu      = new DecalMenuModule(_bridge, _config, _spawner, _placement, _db, _cache,
            loggerFactory.CreateLogger<DecalMenuModule>());
    }

    public bool Init()
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("[MapDecals] Disabled by config — not installing listeners");
            return true;
        }

        _bridge.ModSharp.InstallGameListener(this);
        _placement.Start();

        // Connect DB now; the table is ensured in Connect via CodeFirst.InitTables.
        _db.Connect(_config.Database);

        return true;
    }

    public void PostInit() { }

    public void OnAllModulesLoaded()
    {
        if (!_config.Enabled)
            return;

        _bridge.ResolveModules();
        _menu.Start();

        // If a map is already loaded (hot reload), prime the cache for the current map.
        var map = _bridge.ModSharp.GetMapName();
        if (!string.IsNullOrEmpty(map) && _db.IsConnected)
            _ = LoadMapAsync(map);

        _logger.LogInformation("[MapDecals] Loaded (AdminManager={Admin}, MenuManager={Menu}, DB={Db})",
            _bridge.AdminManager is not null, _bridge.MenuManager is not null, _db.IsConnected);
    }

    public void Shutdown()
    {
        _placement.Stop();
        _bridge.ModSharp.RemoveGameListener(this);
        _spawner.DespawnAll();
        _cache.Clear();
        _db.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // IGameListener
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Precache every configured material so the decal strong-handle resolves at spawn.</summary>
    public void OnResourcePrecache()
    {
        if (_config.Enabled)
            _spawner.PrecacheAll();
    }

    /// <summary>New map active — clear stale decals and load this map's saved set from the DB.</summary>
    public void OnServerActivate()
    {
        if (!_config.Enabled || !_db.IsConnected)
            return;

        _spawner.DespawnAll();
        _cache.Clear();

        var map = _bridge.ModSharp.GetMapName();
        if (!string.IsNullOrEmpty(map))
            _ = LoadMapAsync(map);
    }

    /// <summary>env_decal entities are wiped each round restart — re-spawn the active set.</summary>
    public void OnRoundRestarted()
    {
        if (_config.Enabled)
            _spawner.RespawnAll(_cache.Rows);
    }

    // ──────────────────────────────────────────────────────────────────────────

    private async System.Threading.Tasks.Task LoadMapAsync(string map)
    {
        var rows = await _db.GetByMapAsync(map).ConfigureAwait(false);

        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            _cache.Replace(rows);
            _spawner.RespawnAll(_cache.Rows);
            _logger.LogInformation("[MapDecals] Loaded {Count} decal(s) for map {Map}", rows.Count, map);
        });
    }
}
