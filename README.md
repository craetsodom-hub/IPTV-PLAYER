# IPTV Player for Windows

Premium-oriented Windows IPTV desktop player scaffold with modular architecture, VLC playback integration, and production-ready foundations.

## Current implementation status

Implemented in this baseline:

- Clean multi-project architecture with clear module boundaries
- WPF desktop shell with 3-column premium dark layout
- VLC playback integration through LibVLCSharp
- Playback state handling with buffering/loading overlays
- Double-click fullscreen toggle and escape-to-exit behavior
- Source import panel supporting:
  - Xtream Codes
  - M3U URL
  - M3U file path
  - M3U8 direct stream
- Xtream API auth and category/channel parsing
- M3U and M3U8 parsing pipelines
- Category/channel loading and filtering
- Favorites and recent channels
- Session persistence (last source/category/channel, favorites, recents, mute)
- Source status and expiration display when available
- Structured logging bootstrap with Serilog

## Solution structure

- `src/IptvPlayer.App` - WPF app host and UI
- `src/IptvPlayer.Presentation` - ViewModels and UI logic
- `src/IptvPlayer.Application` - Orchestrators and app use-cases
- `src/IptvPlayer.Domain` - Core domain entities and value objects
- `src/IptvPlayer.Contracts` - Shared contracts and models
- `src/IptvPlayer.Infrastructure` - Imports, catalog storage, persistence
- `src/IptvPlayer.Player.Vlc` - VLC playback service and bridge

## Prerequisites

1. Windows 10 or 11
2. .NET 8 SDK installed and available in PATH
3. Internet access for NuGet restore and network streams

If `dotnet` is not recognized, install the .NET 8 SDK and restart terminal.

## Build and run

```powershell
dotnet restore "IPTV PLAYER.sln"
dotnet build "IPTV PLAYER.sln"
dotnet run --project .\src\IptvPlayer.App\IptvPlayer.App.csproj
```

## Publish (recommended for stable VLC runtime loading)

Use a non-single-file self-contained publish so `libvlc` native files are shipped as normal files:

```powershell
dotnet publish .\src\IptvPlayer.App\IptvPlayer.App.csproj -c Release -r win-x64 --self-contained true -o .\dist\IptvPlayer
```

Run the app from:

- `.\dist\IptvPlayer\IptvPlayer.App.exe`

Do not switch to single-file publish for this app; VLC native runtime loading is most reliable with folder-based publish output.

## Data and logs

- Source catalog (saved subscriptions): `%LocalAppData%\IptvPlayer\catalog\sources.json`
- User session state: `%LocalAppData%\IptvPlayer\state\session.json`
- Logs: `%LocalAppData%\IptvPlayer\logs\iptv-player-*.log`

Imported subscriptions are persisted and remain available across restarts until explicitly deleted from the app.

## Notes

- This is a strong production-oriented foundation built in phases.
- Remaining hardening includes deeper error UX, stronger caching layers, packaging, and additional QA automation.
