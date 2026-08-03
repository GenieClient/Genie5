# Updating Maps and Scripts

The DragonRealms community maintains shared zone maps (and a few helper walking scripts) in a public repository. Genie 5 can pull them directly — no separate download. This page covers updating from the official repo and importing maps from an existing Genie 4 install.

## Where maps live

Zone maps are XML files — one per zone — in your **Maps** folder (`Map1_Crossing.xml`, `Map60_Southern_Trade_Road.xml`, …), alongside a single `ZoneConnections.xml` that describes cross-zone transit links (boats, ferries, climb-walls). See [Application Folders](Application-Folders) for the path, and **Maps ▸ Open Maps Folder** to jump there. Change the location with **Maps ▸ Change Maps Directory…** (or `#config mapdir`).

Genie 5 uses the **same Genie 4 XML map format**, so maps move between the two clients cleanly.

## Updating from the official repo

Choose **Maps ▸ Update from Official Repo…**. Genie 5 fetches the latest zone XML from the community Maps repository via [MapsUpdater](https://github.com/GenieClient/Genie5/blob/main/src/Genie.Core/Update/Updaters/MapsUpdater.cs) (built on the shared [GithubContentsSource](https://github.com/GenieClient/Genie5/blob/main/src/Genie.Core/Update/Sources/GithubContentsSource.cs)) and reports progress in the main game window as `[mapper] …` lines.

### How the merge works

When an update finds a zone you already have, it doesn't blindly overwrite. It loads the upstream zone and carries your locally-collected data forward — most importantly the **`ServerRoomId`** values stamped on nodes as you played through (from `<nav rm="…"/>`). So:

- **Upstream layout changes win** — new rooms, fixed exits, corrected connections come down.
- **Your stamped room ids survive** — the work the mapper did learning your routes isn't lost.
- **New upstream nodes** start without a server-room id and get stamped the first time you visit them.

If a zone file ever gets corrupted by bad edits, delete it from the Maps folder and re-run the update to pull a fresh copy.

### Auto-update on launch

The `updatemapperscripts` setting (see `#config` / **Configuration…**) controls whether helper map scripts are refreshed as part of updates. Repo URLs are configurable via the `maprepo` and `scriptrepo` settings if you point Genie at a fork or mirror. **Help → Update Settings…** decides whether the silent startup check covers Maps and Scripts at all, and whether found updates install by themselves.

## Updating scripts from community repositories

The Updates dialog's **Scripts** tab (see [Keeping Up to Date](Updates#scripts)) subscribes your Scripts folder to one or more GitHub script repositories and pulls new and changed `.cmd` / `.js` files down — like a `git pull`, subfolders included:

- **Your local-only files are never touched.** Only files that exist in a subscribed repo are compared and updated; anything you wrote yourself is invisible to the updater.
- **The community repo is one click away.** [DR-Genie-Scripts](https://github.com/Tirost/DR-Genie-Scripts) — the largest community script collection — ships as a ready-to-enable subscription row.
- **Add your own rows** for any other GitHub script repository (or your fork of the community one).

## Importing maps from a Genie 4 install

If you have a folder of Genie 4 `*.xml` zone files, import them once without going through the community repo:

1. Use the mapper's **Import from Genie 4** path (the same migration covered for rules in [Importing Genie 4 Config](Importing-Genie4-Config), maps side).
2. Point it at your Genie 4 `Maps` folder.
3. Files are brought into your [Maps folder](Application-Folders).

You can still run **Maps ▸ Update from Official Repo…** afterward to pull newer community versions; the merge preserves your stamped data where it can.

## Cross-zone connections

The transit graph the [multi-zone pathfinder](Cross-Zone-Travel) uses lives in `ZoneConnections.xml` at the root of the Maps folder. On first launch Genie 5 seeds a documented starter template there. Edit it via **Maps ▸ Cross-Zone Connections…** (a grid editor), or let the community Maps repo curate richer versions over time. If you delete the file deliberately, Genie 5 won't silently re-create it.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| Every file shows `failed` during an update | No network, a proxy/firewall blocking the repo host, or a badly-skewed system clock (TLS handshakes fail). Local maps are never corrupted by a failed update — failed files are skipped; re-run later. |
| Update seems to do nothing | Everything is already current — unchanged files aren't re-downloaded. |
| A zone won't match your location | Walk through it with AutoMapper enabled so `ServerRoomId`s get stamped, or pull a fresh copy from the repo. |

The updater uses the system's default network configuration, so OS-level proxy settings are honoured automatically.
