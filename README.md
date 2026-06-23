# MapDecals (ModSharp)

Place persistent, per-map decals on world geometry in CS2. An admin selects a predefined decal
from a menu, then **pings a surface** — the ping's world coordinates become the decal origin. Decals
are saved to a database and re-spawned every round (CS2 wipes `env_decal` entities on round restart).

This is a ModSharp port of **[Cruze03/CS2-MapDecals-SwiftlyS2](https://github.com/Cruze03/CS2-MapDecals-SwiftlyS2)**.
All credit for the original concept, placement UX (ping-based), and feature set goes to the original
author; this repository re-implements it on the ModSharp framework.

## How it works

- Each predefined decal is configured with a stable id, display name, a `.vmat` material path, and an
  optional visibility permission (VIP-only decals).
- Materials are precached on map load (`OnResourcePrecache`). A material that isn't precached resolves
  to a null strong-handle and the decal renders invisible.
- Placements are stored per-map in the `cc_mapdecals` table. On map load the rows are loaded into a
  cache; on each round restart the active set is spawned as `env_decal` entities.
- Spawned entities are tracked by entity handle (not raw pointer), so they survive nothing across
  round end — the plugin re-spawns from the cache.

## Commands

| Command | Aliases | Permission | Action |
|---|---|---|---|
| `!mapdecal` | `placedecal`, `placedecals`, `paintmapdecal` | `@mapdecals/admin` | Open the place / edit / remove menu |

Menu flow:

- **Place Decal** → pick a configured decal → ping a surface to place it (saved immediately).
- **Edit Decals** → pick a placed decal → Resize / Enable-Disable / Delete.
- **Reload from DB** → re-read this map's decals and re-spawn.

## Configuration

`<sharp>/configs/mapdecals.json` (auto-created with a default example on first run):

```json
{
  "enabled": true,
  "command": "mapdecal",
  "aliases": ["placedecal", "placedecals", "paintmapdecal"],
  "adminPermission": "@mapdecals/admin",
  "decals": [
    {
      "uniqId": "example_logo",
      "name": "Example Logo",
      "material": "decals/example_logo.vmat",
      "showPermission": "",
      "width": 128,
      "height": 128,
      "depth": 12
    }
  ],
  "database": {
    "type": "mysql",
    "host": "localhost",
    "port": 3306,
    "database": "mapdecals",
    "user": "mapdecals",
    "password": ""
  }
}
```

- `material` is the `.vmat` path relative to the game content root. It must exist and be precached
  (the plugin precaches every configured material automatically).
- `showPermission` (optional) makes a decal VIP-only: it's transmitted only to players holding that
  permission (per-player transmit works because `env_decal` is a normal entity, not a player pawn).

## Build

```bash
dotnet build MapDecals.slnx -c Release
```

Output lands in `.build/modules/MapDecals.Core/` ready for `modsharp-deploy`.

## Requirements

- ModSharp with the AdminManager and MenuManager ecosystem modules loaded (commands and menus need
  them; without AdminManager the admin commands are not registered).
- A MySQL or PostgreSQL database for persistence.

## Notes / differences from the original

- Persistence uses SqlSugar (`CodeFirst.InitTables`) instead of Dommel/Dapper + FluentMigrator; the
  table name (`cc_mapdecals`) and per-map keying are preserved.
- Resize is offered as size presets in the menu (the original allowed free-form width/height); the
  stored width/height/depth fields are identical, so a website/admin can set arbitrary values directly
  in the DB if needed.
