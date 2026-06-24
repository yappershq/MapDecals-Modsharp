<div align="center">
  <h1><strong>MapDecals</strong></h1>
  <p>Place persistent, per-map decals on CS2 world geometry — ping a surface, save it, re-spawn it every round.</p>
</div>

<p align="center">
  <img src="https://img.shields.io/github/stars/yappershq/MapDecals-Modsharp?style=flat&logo=github" alt="Stars">
</p>

---

MapDecals lets an admin stamp predefined decals onto world surfaces in CS2. Pick a decal from a menu, **ping a surface** — the ping's world coordinates become the decal origin — and it's saved to a database and re-spawned automatically every round (CS2 wipes `env_decal` entities on each round restart). This is a ModSharp port of [Cruze03/CS2-MapDecals-SwiftlyS2](https://github.com/Cruze03/CS2-MapDecals-SwiftlyS2).

## 🚀 Install

Copy the build output into your ModSharp install (`<sharp>` = your `sharp` directory):

| From | To |
|------|----|
| `.build/modules/MapDecals.Core/` | `<sharp>/modules/MapDecals.Core/` |

The locale files ship inside the module folder (`.assets/locales/`); no separate copy needed. The config (`<sharp>/configs/mapdecals.json`) is auto-generated with an example decal on first run. Restart the server or change map to load.

**Requires:** the AdminManager and MenuManager ModSharp modules (the admin command and menus need them), plus a MySQL or PostgreSQL database for persistence.

## ⌨️ Commands

| Command | Aliases | Description | Permission |
|---------|---------|-------------|------------|
| `!mapdecal` | `placedecal`, `placedecals`, `paintmapdecal` | Open the place / edit / remove menu | `@mapdecals/admin` |

Menu flow:

- **Place Decal** → pick a configured decal → ping a surface to place it (saved immediately).
- **Edit Decals** → pick a placed decal → Resize / Enable-Disable / Delete.
- **Reload from DB** → re-read this map's decals and re-spawn.

Command, aliases and permission are all configurable (see below).

## ⚙️ Configuration

`<sharp>/configs/mapdecals.json` (auto-generated on first run):

| Setting | Default | Meaning |
|---------|---------|---------|
| `enabled` | `true` | Master switch. When false, the plugin loads but places/spawns nothing. |
| `command` | `"mapdecal"` | Primary admin command (no prefix) that opens the menu. |
| `aliases` | `["placedecal", "placedecals", "paintmapdecal"]` | Extra commands that also open the menu. |
| `adminPermission` | `"@mapdecals/admin"` | Permission required to place/edit/remove decals. |
| `decals` | example entry | List of predefined decals an admin may place (see below). |
| `database` | `mysql @ localhost` | Connection block — `type` (`mysql`/`postgresql`), `host`, `port`, `database`, `user`, `password`. |

Each entry in `decals`:

| Field | Default | Meaning |
|-------|---------|---------|
| `uniqId` | — | Stable id used to reference the decal from saved DB rows. Must be unique. |
| `name` | — | Display name shown in menus. |
| `material` | — | `.vmat` material path (e.g. `decals/my_decal.vmat`). Must exist; precached automatically on map load. |
| `showPermission` | `""` | Optional — makes the decal VIP-only: transmitted only to players holding this permission. |
| `width` / `height` | `128` | Default decal size (units) when first placed. |
| `depth` | `12` | Default projection depth (units). |

## 🔧 How it works

Configured materials are precached on map load (`OnResourcePrecache`); a material that isn't precached resolves to a null handle and renders invisible. Placements are stored per-map in the `cc_mapdecals` table and cached on map load. Because CS2 clears `env_decal` entities on every round restart, the plugin re-spawns the active set from cache on `OnRoundRestarted`. Spawned entities are tracked by entity handle, and `showPermission` decals get per-player visibility via the transmit manager (`env_decal` is a normal entity, not a player pawn, so per-player transmit works).

## 📦 Build

```bash
dotnet build MapDecals.slnx -c Release
```

Outputs `.build/modules/MapDecals.Core/MapDecals.dll` plus its bundled NuGet dependencies and `.assets/locales/`.

## 🙏 Credits

Port of [Cruze03/CS2-MapDecals-SwiftlyS2](https://github.com/Cruze03/CS2-MapDecals-SwiftlyS2). All credit for the original concept, ping-based placement UX, and feature set goes to the original author; this repository re-implements it on the ModSharp framework.

Persistence here uses SqlSugar (`CodeFirst.InitTables`) rather than the upstream's Dapper/FluentMigrator, but the table name (`cc_mapdecals`) and per-map keying are preserved.

---

<div align="center">
  <p>Made with ❤️ by <a href="https://github.com/yappershq">yappershq</a></p>
  <p>⭐ Star this repo if you find it useful!</p>
</div>
