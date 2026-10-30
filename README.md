# Bulk Metadata Editor

A Jellyfin server plugin for editing metadata across many library items at once,
instead of opening the Edit Metadata dialog for each one.

> **Requires Jellyfin 12.0.** This plugin targets `net10.0` and is built against the
> Jellyfin 12 assemblies. It will not load on Jellyfin 10.11 or earlier.

## Features

- **Filter** the library by type: movies, shows, seasons, episodes, collections, albums,
  artists, songs, music videos, videos, trailers, books, audiobooks, playlists and photos.
  You can also filter by library, title search, file name, genre, tag and year. Other
  filters find items missing data (overview, parental rating, IMDb/TMDb/TVDb ID) or limit
  the list by lock state. Results are sortable and paged.
- **File name search** matches part of an item's file name, or its folder name for shows,
  seasons and albums. It combines with every other filter.
- **Choose the grid columns.** Pick any of about 40 metadata columns, from release date and
  studios to external IDs and file path. The default is Type, Year, Rating, Genres, Tags and
  Locked. Your choice is remembered in the browser.
- **Select** individual items, a page, or every item that matches the filters. The
  selection is kept as you page through results.
- **Edit the fields from Jellyfin's Edit Metadata dialog:**
  - **General:** title, original title, sort title, date added, status, release date, year,
    air days and time, end date, run time, community and critic ratings, original
    language, tagline, overview
  - **Parental rating** and custom rating
  - **Lists:** genres, tags, studios, production locations, people
  - **Music:** album, album artists, artists
  - **Numbering:** episode/season/track number, airs before/after
  - **Video:** aspect ratio, 3D format
  - **External provider IDs**
  - **Metadata settings:** preferred language, country, display order
  - **Locking:** item lock and locked fields
- **Only ticked fields change.** List fields can **add**, **remove** or **replace** values.
- **Lock edited fields** (optional): your changes survive a later metadata refresh.
- **Same side effects as Jellyfin's own editor:**
  - Rating and tag changes on a show, season or album carry down to its children.
  - Locking a folder locks its children.
  - Changing a show's display order queues a refresh.
- **Per-type report.** A field that does not apply to an item's type is skipped for that
  item and reported after the update. For example, *Status* only applies to shows.
- **Audit log** at the bottom of the page. Each entry records one bulk edit: when it ran,
  who ran it, which fields changed and how, and the result for each item, including
  failures and skipped fields. The log keeps the 100 most recent edits.

## Installation

### From the plugin repository

In Jellyfin, open **Dashboard → Plugins → Repositories** and add:

```
https://raw.githubusercontent.com/JuiceDev82/jellyfin-plugin-bulkmetadataeditor/main/manifest.json
```

**Bulk Metadata Editor** then appears in the catalog.

### Manually

Build the plugin, then copy the output into a subfolder of your Jellyfin `plugins` directory:

```bash
dotnet publish Jellyfin.Plugin.BulkMetadataEditor -c Release
```

Copy `Jellyfin.Plugin.BulkMetadataEditor.dll` and `manifest.json` into a new folder such as
`plugins/BulkMetadataEditor/`, then restart the server. The `plugins` directory is typically:

| Platform | Path |
| --- | --- |
| Linux | `/var/lib/jellyfin/plugins/` |
| Windows | `%ProgramData%\Jellyfin\Server\plugins\` |
| Docker | `/config/plugins/` |

After restarting, **Bulk Metadata Editor** appears in the main menu of the web UI.

## Building

Requires the **.NET 10 SDK**. The Jellyfin assemblies come from nuget.org and restore
automatically:

```bash
dotnet build Jellyfin.Plugin.BulkMetadataEditor.sln -c Release
```

They are referenced with `ExcludeAssets="runtime"`, so they are used for compilation only
and are never copied into the plugin output. The server supplies them at runtime.

## Releasing

1. Bump `AssemblyVersion` in the project file.
2. Push an annotated tag. The tag message becomes the changelog:

   ```bash
   git tag -a v1.0.0 -m "Initial release"
   git push origin v1.0.0
   ```

The release workflow then builds the plugin, attaches the zip to a GitHub release, adds the
version to `manifest.json` on `main`, and checks that the published zip matches the
manifest checksum.

## Permissions

Every `/BulkMetadataEditor/*` endpoint is gated behind
`[Authorize(Policy = Policies.RequiresElevation)]`, so only Jellyfin administrators can call
them. Jellyfin does not install a global authorization filter. Removing that attribute would
expose library-mutating endpoints to anonymous callers.

The web UI uses `ApiClient`, which sends the standard `Authorization: MediaBrowser ...` header.

## API

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/BulkMetadataEditor/Update` | Apply metadata changes to the given item IDs |
| `GET` | `/BulkMetadataEditor/AuditLog` | Read the audit log, newest first |
| `POST` | `/BulkMetadataEditor/AuditLog/Clear` | Clear the audit log |
| `GET` | `/BulkMetadataEditor/Search` | List the IDs and types of every item whose file or folder name contains `fileName`, with the same filters as `/Items` |

```json
{
  "OperationId": "<guid>",
  "ItemIds": ["<guid>", "..."],
  "LockEditedFields": false,
  "Changes": {
    "Genres": { "Mode": "Add", "Values": ["Noir"] },
    "OfficialRating": { "Value": "PG-13" },
    "Overview": { "Value": null },
    "People": { "Mode": "Remove", "People": [{ "Name": "Jane Doe", "Type": null }] },
    "ProviderIds": { "Set": {}, "Remove": ["Tvdb"] }
  }
}
```

Only fields present in `Changes` are touched, and a `null` value clears the field. The
response lists, per item, which fields were applied or skipped, and any error. The web UI
sends updates in batches of 20 items. Batches that share an `OperationId` are recorded as a
single audit log entry. Omitting it records each request as its own entry.

## Notes

- The audit log is stored as `plugins/configurations/Jellyfin.Plugin.BulkMetadataEditor.audit-log.json`,
  beside the plugin's settings. Each installed version gets its own install folder, so this
  location keeps the log when the plugin is upgraded.
- Each entry keeps per-item results for its first 500 items. Its counts always cover every
  item.
- Jellyfin cannot filter on part of a path. A file name search therefore runs the other
  filters in the database and matches names in memory on the server. The page caches the
  matching IDs until a filter changes and loads each page's details from `/Items`.

## License

[GPL-3.0](LICENSE)
