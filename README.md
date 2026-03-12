# sldl web

A web interface for [slsk-batchdl (sldl)](https://github.com/fiso64/slsk-batchdl) — batch download music from Soulseek using Spotify playlists, CSV files, or search queries.

## Features

- Paste a Spotify playlist URL, CSV content, or search query
- Real-time download progress via WebSocket
- Track-by-track status (Downloaded / Failed / Queued)
- Download completed files individually or as a ZIP
- Dark UI, single-user, local-first

## Architecture

```
┌─────────────┐     WebSocket / REST     ┌──────────────┐     subprocess     ┌──────┐
│  Next.js UI │  ◄──────────────────────► │  .NET API    │  ──────────────►  │ sldl │
│  (port 3000)│                           │  (port 5000) │                   │ CLI  │
└─────────────┘                           └──────────────┘                   └──────┘
                                                │
                                           ┌────▼────┐
                                           │downloads│
                                           │ volume  │
                                           └─────────┘
```

- **sldl** is included as a git submodule and built as a standalone binary
- The .NET API server spawns sldl as a child process per job, parses stdout and monitors the index file for track status
- The Next.js frontend connects via WebSocket for real-time updates, with REST polling as fallback

## Quick Start (Local Development)

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js 20+](https://nodejs.org/)
- A Soulseek account (create one at https://www.slsknet.org/)
- (Optional) Spotify API credentials for playlist URL support

### 1. Clone with submodules

```bash
git clone --recurse-submodules <repo-url>
cd slsk-batch-downloader
```

### 2. Build sldl

```bash
cd sldl
dotnet publish slsk-batchdl/slsk-batchdl.csproj -c Release -o ../bin
cd ..
```

### 3. Configure

```bash
cp .env.example .env
# Edit .env with your Soulseek credentials (required)
# Add Spotify API credentials if you want playlist URL support
```

### 4. Run the API server

```bash
cd server
SLDL__BINARYPATH=../bin/sldl SLDL__DOWNLOADPATH=../downloads dotnet run
```

### 5. Run the frontend

```bash
cd web
npm install
npm run dev
```

Open [http://localhost:3000](http://localhost:3000)

## Quick Start (Docker)

```bash
cp .env.example .env
# Edit .env with your credentials

docker compose up --build
```

Open [http://localhost:3000](http://localhost:3000)

Downloads will be saved to `./downloads/` on your host machine.

## Configuration

All configuration is via environment variables (or `.env` file):

| Variable | Required | Description |
|----------|----------|-------------|
| `SLDL__USERNAME` | Yes | Soulseek username |
| `SLDL__PASSWORD` | Yes | Soulseek password |
| `SPOTIFY__CLIENTID` | For Spotify | Spotify API client ID |
| `SPOTIFY__CLIENTSECRET` | For Spotify | Spotify API client secret |
| `SLDL__DOWNLOADPATH` | No | Download directory (default: `./downloads`) |
| `SLDL__PREFERREDFORMAT` | No | Preferred audio format (default: `mp3`) |
| `SLDL__MINBITRATE` | No | Minimum bitrate (default: `200`) |
| `NEXT_PUBLIC_API_URL` | No | API URL for frontend (default: `http://localhost:5000`) |

### Getting Spotify API Credentials

1. Go to [Spotify Developer Dashboard](https://developer.spotify.com/dashboard)
2. Create a new app
3. Copy the Client ID and Client Secret

## Project Structure

```
.
├── sldl/                  # git submodule: slsk-batchdl
├── server/                # .NET 8 API server
│   ├── Program.cs         # API routes (REST + WebSocket)
│   ├── Models/Job.cs      # Job and track models
│   └── Services/
│       ├── JobManager.cs      # Spawns sldl, monitors progress
│       └── WebSocketManager.cs
├── web/                   # Next.js frontend
│   └── src/
│       ├── app/           # Pages (home, job detail)
│       ├── components/    # InputForm, JobList, TrackList
│       └── lib/           # API client, WebSocket hook
├── docker-compose.yml
├── Dockerfile.api
├── Dockerfile.web
└── .env.example
```

## Download Location

When running locally (or via Docker with the volume mount), files download to your machine's filesystem. The default is `./downloads/` organized by date.

For remote deployment (Fly.io, Railway, VPS), files are stored on the server. You can download them via the web UI (individual files or ZIP).

## Future Improvements

- Persistent job storage (SQLite)
- File browser for downloaded music
- Quality/format selection per job
- Fly.io deployment config
- Progress bars for individual file downloads (requires sldl modifications)
- Audio preview/player in the UI

## License

This project wraps [slsk-batchdl](https://github.com/fiso64/slsk-batchdl) which is GPL-3.0 licensed.
