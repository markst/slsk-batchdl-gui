# sldl web

A web interface for [slsk-batchdl (sldl)](https://github.com/fiso64/slsk-batchdl) — batch download music from Soulseek using Spotify playlists, CSV files, or search queries.

The UI connects to a running **sldl daemon** over HTTP REST + SignalR, so the daemon and the UI can be deployed and restarted independently.

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
┌──────────────────────────────────────────────────────┐
│               Blazor Server App (sldl-web)           │
│                                                      │
│  Browser  ◄──── SignalR (/downloadHub) ────►  DownloadService  │
│  (Razor)                                    │        │
│                                    SldlEventBridge   │
│                                    (BackgroundService)│
└─────────────────────────────────────┬────────────────┘
                           HTTP REST  │  SignalR
                         + SignalR    │  (/api/events)
                                      ▼
                    ┌─────────────────────────────┐
                    │     sldl daemon              │
                    │  (slsk-batchdl --server)     │
                    │  http://localhost:5030        │
                    └─────────────────────────────┘
```

- **`DaemonLauncherService`** auto-starts the sldl daemon on app launch (bundled binary at `bin/sldl`, or the submodule build output in dev) and kills it on exit — users never need to start it manually
- The daemon exposes an HTTP REST API at `http://localhost:5030` (configurable via `SldlDaemonUrl` in `appsettings.json`)
- **`DownloadService`** submits jobs to the daemon via `POST api/jobs/extract|downloads/song|downloads/album` and polls `GET api/workflows/{id}` as a fallback
- **`SldlEventBridge`** subscribes to the daemon's SignalR hub at `/api/events` (`serverEvent` method) and pushes live `song.state-changed`, `download.progress`, and `workflow.upserted` events to the browser
- **Electron.NET** wraps the Blazor Server app in an Electron window for a native desktop experience
- The sldl source is included as a git submodule (pinned at the AGPL-3.0 relicense commit); `Sldl.Api` contract types are referenced directly — no local DTO mirror

## Quick Start (Desktop App)

Pre-built binaries for macOS and Windows are available on the [Releases](../../releases) page. Download the appropriate file for your platform and run it — no installation required.

### Prerequisites (building from source)

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
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

#### Spotify API credentials (optional)

To bake Spotify credentials into the build so users don't have to enter them, copy the template and fill in your keys:

```bash
cp app/spotify.local.props.example app/spotify.local.props
# Edit app/spotify.local.props with your Client ID and Secret
```

This file is gitignored. If omitted, users can still enter credentials manually in Settings.

#### Build

```bash
cd app

# macOS (.dmg + .zip)
electronize build /target osx /PublishReadyToRun false

# Windows (.exe installer)
electronize build /target win /PublishReadyToRun false
```

Built packages are written to `app/obj/desktop/{osx,win}/dist/`.

> **Note:** macOS builds are unsigned. Recipients will need to right-click → Open the app the first time.

## Quick Start (Local — web only)

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A Soulseek account (create one at https://www.slsknet.org/)

### Configure the daemon URL (optional)

By default the app connects to `http://localhost:5030`. Override in `app/appsettings.json`:

```json
{
  "SldlDaemonUrl": "http://localhost:5030"
}
```

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
├── sldl/                          # git submodule: slsk-batchdl (pinned, AGPL-3.0)
├── app/                           # .NET 10 Blazor Server app
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
│   ├── Hubs/DownloadHub.cs        # SignalR hub (/downloadHub)
│   ├── Models/
│   │   ├── DownloadJob.cs         # Job + track models
│   │   ├── DaemonStateMapper.cs   # Maps ServerJobState → app strings
│   │   └── SldlEventEnvelope.cs   # Local envelope for daemon SignalR events
│   ├── Services/
│   │   ├── AuthService.cs         # Soulseek login validation + auth state
│   │   ├── DaemonLauncherService.cs # Auto-starts/stops the sldl daemon process
│   │   ├── DownloadService.cs     # Job management, calls sldl daemon via HTTP
│   │   ├── SldlEventBridge.cs     # Subscribes to daemon SignalR event hub
│   │   ├── JobRestorer.cs         # Restores persisted jobs from disk on startup
│   │   └── SettingsService.cs     # Persists settings to settings.json
│   └── wwwroot/app.css            # Dark theme styles
└── .github/workflows/
    └── build-desktop.yml          # CI: build Electron packages for Windows & macOS
```

## License

This project wraps [slsk-batchdl](https://github.com/fiso64/slsk-batchdl) which is AGPL-3.0 licensed.

