# sldl web

A web interface for [slsk-batchdl (sldl)](https://github.com/fiso64/slsk-batchdl) — batch download music from Soulseek using Spotify playlists, CSV files, or search queries.

## Features

- Paste a Spotify playlist URL, CSV content, or search query
- Real-time download progress via SignalR
- Track-by-track status with progress bars
- Dark theme UI
- Single .NET process — no separate frontend server

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

## Quick Start (Local)

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- A Soulseek account (create one at https://www.slsknet.org/)
- (Optional) Spotify API credentials for playlist URL support

### 1. Clone with submodules

```bash
git clone --recurse-submodules <repo-url>
cd slsk-batch-downloader
```

### 2. Configure

Edit `app/appsettings.json` with your credentials:

```json
{
  "Sldl": {
    "Username": "your_soulseek_username",
    "Password": "your_soulseek_password",
    "DownloadPath": "./downloads",
    "PreferredFormat": "mp3",
    "MinBitrate": "200"
  },
  "Spotify": {
    "ClientId": "",
    "ClientSecret": ""
  }
}
```

### 3. Run

```bash
dotnet run --project app
```

Open [http://localhost:5223](http://localhost:5223)

## Configuration

### appsettings.json

| Key | Required | Description |
|-----|----------|-------------|
| `Sldl:Username` | Yes | Soulseek username |
| `Sldl:Password` | Yes | Soulseek password |
| `Sldl:DownloadPath` | No | Download directory (default: `./downloads`) |
| `Sldl:PreferredFormat` | No | Preferred audio format (default: `mp3`) |
| `Sldl:MinBitrate` | No | Minimum bitrate (default: `200`) |
| `Spotify:ClientId` | For Spotify | Spotify API client ID |
| `Spotify:ClientSecret` | For Spotify | Spotify API client secret |

### Getting Spotify API Credentials

1. Go to [Spotify Developer Dashboard](https://developer.spotify.com/dashboard)
2. Create a new app
3. Copy the Client ID and Client Secret

## Project Structure

```
.
├── sldl/                          # git submodule: slsk-batchdl
├── app/                           # .NET 8 Blazor Server app
│   ├── Program.cs                 # App startup, service registration
│   ├── Components/
│   │   ├── Layout/MainLayout.razor
│   │   └── Pages/
│   │       ├── Home.razor         # Input form + job list
│   │       └── Job.razor          # Track list with live progress
│   ├── Hubs/DownloadHub.cs        # SignalR hub
│   ├── Models/DownloadJob.cs      # Job + track models
│   ├── Services/
│   │   ├── DownloadService.cs     # Job management, calls sldl in-process
│   │   └── SignalRProgressReporter.cs  # IProgressReporter → SignalR
│   └── wwwroot/app.css            # Dark theme styles
└── .env.example
```

## License

This project wraps [slsk-batchdl](https://github.com/fiso64/slsk-batchdl) which is GPL-3.0 licensed.
