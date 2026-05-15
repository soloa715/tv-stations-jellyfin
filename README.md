# TV Stations — Jellyfin Plugin

A Jellyfin plugin that creates virtual Live TV channels from your media library, organized by genre. Movie channels (100+) and Show channels (200+) are generated automatically based on the genres present in your library, complete with an Electronic Program Guide (EPG).

## Features

- **Automatic genre channels** — one channel per genre for Movies and one per genre for Shows
- **EPG support** — shows what is playing now and what is coming up, calculated from your library's runtime metadata
- **Zero configuration** — works out of the box after installation
- **Configurable** — set minimum items per channel, maximum genres, enable/disable Movies or Shows
- **No external services** — streams directly from your existing library files

## Installation via Jellyfin GUI

1. Open Jellyfin dashboard → **Plugins** → **Repositories**
2. Click **Add** and enter the repository URL:
   ```
   https://raw.githubusercontent.com/soloa715/tv-stations-jellyfin/main/manifest.json
   ```
3. Go to **Catalog**, find **TV Stations**, and click **Install**
4. Restart Jellyfin
5. The genre channels will appear automatically in **Live TV**

## Channel Layout

| Range | Type | Example |
|-------|------|---------|
| 100–199 | Movie channels | 101 Action Movies, 102 Comedy Movies |
| 200–299 | Show channels | 201 Drama Shows, 202 Sci-Fi Shows |

## How It Works

The plugin registers as a Live TV service. When Jellyfin queries for channels, it reads your library's genres and creates one channel per genre×type combination. The program schedule is calculated deterministically from a fixed epoch — items loop continuously in alphabetical order, so the guide is consistent across server restarts.

## Configuration

Go to **Dashboard → Plugins → TV Stations** to configure:

- **Enable Movie Channels** — toggle ch 100+ group
- **Enable Show Channels** — toggle ch 200+ group
- **Minimum Items Per Channel** — skip genres with fewer items than this (default: 2)
- **Maximum Genres Per Type** — cap the total number of genre channels per type (default: 50)

## Requirements

- Jellyfin 10.10.x or later
- Media must have genre metadata set (use a metadata scraper if needed)
- Items need file system paths accessible to the Jellyfin server

## Manual Installation

1. Download the ZIP from the [latest release](https://github.com/soloa715/tv-stations-jellyfin/releases/latest)
2. Extract `Jellyfin.Plugin.TvStations.dll` into your Jellyfin plugins directory:
   - Linux: `/var/lib/jellyfin/plugins/TvStations/`
   - Windows: `%PROGRAMDATA%\Jellyfin\Server\plugins\TvStations\`
3. Restart Jellyfin

## Building from Source

```bash
git clone https://github.com/soloa715/tv-stations-jellyfin.git
cd tv-stations-jellyfin
dotnet build --configuration Release
```

The DLL will be at `bin/Release/net8.0/Jellyfin.Plugin.TvStations.dll`.
