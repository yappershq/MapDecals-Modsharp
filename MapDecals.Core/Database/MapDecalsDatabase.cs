using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MapDecals.Configuration;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace MapDecals.Database;

/// <summary>
/// Self-contained SqlSugar access to the <c>cc_mapdecals</c> table. All calls are async and run off
/// the game thread; callers marshal results back via <c>IModSharp.InvokeFrameAction</c>.
/// </summary>
internal sealed class MapDecalsDatabase : IDisposable
{
    private readonly ILogger<MapDecalsDatabase> _logger;
    private SqlSugarScope?                       _db;

    public bool IsConnected => _db is not null;

    public MapDecalsDatabase(ILogger<MapDecalsDatabase> logger) => _logger = logger;

    public bool Connect(DatabaseConfig cfg)
    {
        try
        {
            var dbType = cfg.Type.ToLowerInvariant() switch
            {
                "mysql"      => DbType.MySql,
                "postgresql" => DbType.PostgreSQL,
                _            => throw new NotSupportedException($"Unsupported DB type '{cfg.Type}' (mysql|postgresql)"),
            };

            // Cap pool size — many plugins share the same MySQL box; default (100) exhausts max_connections.
            var conn = dbType switch
            {
                DbType.MySql => $"Server={cfg.Host};Port={cfg.Port};Database={cfg.Database};User={cfg.User};Password={cfg.Password};AllowPublicKeyRetrieval=true;Maximum Pool Size=4;Minimum Pool Size=0;",
                _            => $"Host={cfg.Host};Port={cfg.Port};Database={cfg.Database};Username={cfg.User};Password={cfg.Password};Maximum Pool Size=4;Minimum Pool Size=0;",
            };

            _db = new SqlSugarScope(new ConnectionConfig
            {
                DbType                = dbType,
                ConnectionString      = conn,
                IsAutoCloseConnection = true,
                InitKeyType           = InitKeyType.Attribute,
            });

            // Probe so a bad config fails loudly at load instead of on first query.
            _ = _db.Ado.GetInt("SELECT 1");
            _db.CodeFirst.InitTables<MapDecalRow>();

            _logger.LogInformation("[MapDecals] Connected to DB {Host}/{Db}", cfg.Host, cfg.Database);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[MapDecals] Failed to connect to DB — persistence disabled");
            _db = null;
            return false;
        }
    }

    /// <summary>All decals saved for a given map (active and inactive). Empty on failure.</summary>
    public async Task<List<MapDecalRow>> GetByMapAsync(string map)
    {
        if (_db is null || string.IsNullOrEmpty(map))
            return [];

        try
        {
            return await _db.Queryable<MapDecalRow>()
                .Where(r => r.Map == map)
                .ToListAsync()
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[MapDecals] Load for map '{Map}' failed", map);
            return [];
        }
    }

    /// <summary>Insert a new row and backfill its <see cref="MapDecalRow.Id"/>. Returns false on failure.</summary>
    public async Task<bool> InsertAsync(MapDecalRow row)
    {
        if (_db is null) return false;
        try
        {
            // InsertReturnIdentityAsync backfills the identity; plain InsertAsync returns row-count only.
            var id = await _db.Insertable(row).ExecuteReturnBigIdentityAsync().ConfigureAwait(false);
            row.Id = id;
            return true;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[MapDecals] Insert failed (map={Map}, decal={Decal})", row.Map, row.DecalId);
            return false;
        }
    }

    /// <summary>Update an existing row by primary key. Returns false on failure.</summary>
    public async Task<bool> UpdateAsync(MapDecalRow row)
    {
        if (_db is null) return false;
        try
        {
            await _db.Updateable(row).ExecuteCommandAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[MapDecals] Update failed (id={Id})", row.Id);
            return false;
        }
    }

    /// <summary>Delete a row by primary key. Returns false on failure.</summary>
    public async Task<bool> DeleteAsync(long id)
    {
        if (_db is null) return false;
        try
        {
            await _db.Deleteable<MapDecalRow>().Where(r => r.Id == id).ExecuteCommandAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "[MapDecals] Delete failed (id={Id})", id);
            return false;
        }
    }

    public void Dispose()
    {
        _db?.Dispose();
        _db = null;
    }
}
