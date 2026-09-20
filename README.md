# Whose IPTV - Windows Desktop Player

A modern, high-performance Windows IPTV player built with WPF, .NET 8, and embedded LibVLC. Designed for smooth streaming, resilient playback, and a clean TV/desktop user experience.

---

## Highlights & Features

- **Embedded VLC Playback**: 100% in-app rendering via direct HWND video host—no detached windows, flicker-free fullscreen transitions, and low latency.
- **Multi-Source Support**:
  - Xtream Codes API (Live Streams, VOD Movies, Series, Categories, EPG)
  - M3U and M3U8 playlist URLs
  - Local M3U playlist files
  - Direct stream links
- **Full VOD & Series Experience**:
  - Movie details, posters, and playback progress
  - TV series seasons, episodes, and resume/continue watching tracking
- **Live Sports Hub**:
  - Integrated sports events schedule and live scores
  - Intelligent channel matching for football, basketball, motorsports, tennis, and more
  - Global club and tournament popularity rankings
- **Multilingual Localization**:
  - Fully localized UI with 20+ supported languages (English, Arabic, French, German, Spanish, Italian, Japanese, Russian, Portuguese, Turkish, and more)
- **Advanced Player Controls**:
  - Sleek hover/fullscreen HUD with channel switching, aspect ratio toggle, audio track selection, and volume controls
  - Picture-in-picture style mini-player
  - Keyboard shortcuts (space to pause, double-click for fullscreen, escape to exit)
- **Store-Ready MSIX Packaging**:
  - Canonical Windows Application Packaging Project (`WhoseIptv.Package.wapproj`) ready for the Microsoft Store.

---

## Architecture & Solution Structure

```
├── src/
│   ├── IptvPlayer.App/             # WPF Application shell, views, and custom controls
│   ├── IptvPlayer.Presentation/    # ViewModels, UI state, localization resources, and ranking logic
│   ├── IptvPlayer.Application/     # Use cases, catalog orchestrators, and playback coordination
│   ├── IptvPlayer.Domain/          # Core domain models and entities
│   ├── IptvPlayer.Contracts/       # Shared interfaces, DTOs, and channel/stream models
│   ├── IptvPlayer.Infrastructure/  # Source import pipelines, persistence, and local storage
│   ├── IptvPlayer.Player.Vlc/      # LibVLCSharp playback implementation and native bridge
│   └── WhoseIptv.Package/          # Windows Application Packaging (WAP) project for Store/MSIX
├── tests/
│   └── IptvPlayer.Presentation.RegressionTests/ # Automated regression tests
├── website/                        # Product landing page
└── plans/                          # Architectural roadmaps and design docs
```

---

## Getting Started

### Prerequisites
- Windows 10 (version 1809+) or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022 / Build Tools (if packaging MSIX via `.wapproj`)

### Build the Solution

```powershell
dotnet restore "IPTV PLAYER.sln"
dotnet build "IPTV PLAYER.sln"
```

### Run the Desktop App

```powershell
dotnet run --project .\src\IptvPlayer.App\IptvPlayer.App.csproj
```

### Run Automated Tests

```powershell
dotnet run --project .\tests\IptvPlayer.Presentation.RegressionTests\IptvPlayer.Presentation.RegressionTests.csproj
```

---

## Publishing

### Standalone Folder Publish (Recommended for local distribution)

```powershell
dotnet publish .\src\IptvPlayer.App\IptvPlayer.App.csproj -c Release -r win-x64 --self-contained true -o .\dist\IptvPlayer
```

The output binary will be located at `.\dist\IptvPlayer\IptvPlayer.App.exe`. Folder-based publish ensures all LibVLC native libraries are cleanly loaded.

### Store Package (MSIX)

Run the packaging script using Visual Studio MSBuild:

```cmd
Build-Store-Package.cmd
```

---

## Local Data & Persistence

- **Saved Subscriptions**: `%LocalAppData%\IptvPlayer\catalog\sources.json`
- **Session State**: `%LocalAppData%\IptvPlayer\state\session.json`
- **Application Logs**: `%LocalAppData%\IptvPlayer\logs\iptv-player-*.log`

All user credentials and tokens stored locally are kept in user-protected application storage and are never tracked in Git.
