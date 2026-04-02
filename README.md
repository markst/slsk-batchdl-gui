# sldl web

A web interface for [slsk-batchdl (sldl)](https://github.com/fiso64/slsk-batchdl) — batch download music from Soulseek using Spotify playlists, CSV files, or search queries.

## Features

- **Login page** — validates your Soulseek credentials before granting access; auto-logs in on restart if saved credentials are valid
- Paste a Spotify playlist URL, CSV content, or search query
- Real-time download progress via SignalR
- Track-by-track status with progress bars
- In-app **Settings** page for all configuration (credentials, download prefs, Spotify API keys)
- Dark theme UI
- Single .NET process — no separate frontend server
- **Cross-platform desktop app** via [Electron.NET](https://github.com/ElectronNET/Electron.NET) — runs on macOS, Windows, and Linux

## Architecture

```
┌──────────────────────────────────────────────┐
│              Blazor Server App               │
│                                              │
│  Browser  ◄──── SignalR ────►  DownloadService │
│  (Razor)                       │              │
│                         DownloaderApplication │
│                         (sldl in-process)     │
│                                │              │
│                           ┌────▼────┐         │
│                           │downloads│         │
│                           └─────────┘         │
└──────────────────────────────────────────────┘
```

- **sldl** is included as a git submodule and referenced as a project dependency
- The Blazor Server app calls sldl's `DownloaderApplication` directly in-process
- A `SignalRProgressReporter` implements sldl's `IProgressReporter` interface to push real-time updates to the browser
- **Electron.NET** wraps the Blazor Server app in an Electron window for a native desktop experience

## Quick Start (Desktop App)

Pre-built binaries for macOS and Windows are available on the [Releases](../../releases) page. Download the appropriate file for your platform and run it — no installation required.

### Prerequisites (building from source)

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js 20+](https://nodejs.org/)
- A Soulseek account (create one at https://www.slsknet.org/)
- (Optional) Spotify API credentials for playlist URL support

### 1. Clone with submodules

```bash
git clone --recurse-submodules <repo-url>
cd slsk-batchdl-gui
```

### 2. Install the Electron.NET CLI

```bash
dotnet tool install ElectronNET.CLI -g
```

### 3. Run as a desktop app

```bash
cd app
electronize start
```

This launches a native desktop window running the Blazor UI.

### Build a distributable package

```bash
# Windows (.exe installer)
electronize build /target win

# macOS (.dmg)
electronize build /target osx

# macOS Apple Silicon
electronize build /target osx /electron-arch arm64
```

Built packages are written to `app/bin/Desktop/`.

## Quick Start (Local — web only)

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- A Soulseek account (create one at https://www.slsknet.org/)

### Run

```bash
dotnet run --project app
```

Open [http://localhost:5223](http://localhost:5223)

On first launch you'll be presented with a login page — enter your Soulseek credentials. All other settings (download path, format, Spotify API keys, etc.) can be configured from the **Settings** page inside the app.

### Getting Spotify API Credentials

1. Go to [Spotify Developer Dashboard](https://developer.spotify.com/dashboard)
2. Create a new app
3. Copy the Client ID and Client Secret

## Project Structure

```
.
├── sldl/                          # git submodule: slsk-batchdl
├── app/                           # .NET 8 Blazor Server app
│   ├── Program.cs                 # App startup, service registration, Electron.NET
│   ├── electron.manifest.json     # Electron app configuration
│   ├── Components/
│   │   ├── Layout/
│   │   │   ├── MainLayout.razor   # Auth-gated layout with logout
│   │   │   └── LoginLayout.razor  # Minimal layout for login page
│   │   └── Pages/
│   │       ├── Login.razor        # Soulseek credential validation
│   │       ├── Home.razor         # Input form + job list
│   │       └── Job.razor          # Track list with live progress
│   ├── Hubs/DownloadHub.cs        # SignalR hub
│   ├── Models/DownloadJob.cs      # Job + track models
│   ├── Services/
│   │   ├── AuthService.cs         # Soulseek login validation + auth state
│   │   ├── DownloadService.cs     # Job management, calls sldl in-process
│   │   ├── SettingsService.cs     # Persists settings to settings.json
│   │   └── SignalRProgressReporter.cs  # IProgressReporter → SignalR
│   └── wwwroot/app.css            # Dark theme styles
└── .github/workflows/
    └── build-desktop.yml          # CI: build Electron packages for Windows & macOS
```

## License

This project wraps [slsk-batchdl](https://github.com/fiso64/slsk-batchdl) which is GPL-3.0 licensed.

