# FreeFlow

**Free, open-source Windows utility** that automatically watches a live stats file and pushes updates to multiple FTP/FTPS/SFTP destinations.

Built for college athletic departments who are tired of manual uploads and vendor lock-in.

## Features (MVP)
- Watch a single stats file (XML, JSON, etc.)
- Auto-upload on change with smart "settle" logic
- Support for FTP and FTPS
- Multiple destinations
- Clean WPF UI (Windows 10+)
- System tray support (coming soon)
- Fully open source (MIT)

## Getting Started

### On macOS (Development)
```bash
# Clone or download this repo
cd FreeFlow
dotnet restore
dotnet build
```

The `FreeFlow.Core` library can be fully developed and tested on macOS.

### On Windows (Full App)
1. Open `FreeFlow.sln` in Visual Studio 2022 or newer
2. Set `FreeFlow.Wpf` as startup project
3. Build and run

## Architecture
- `FreeFlow.Core` — Cross-platform logic (works on Mac + Windows)
- `FreeFlow.Wpf` — Windows UI layer

## Roadmap
- v0.1: Core file watcher + multi-destination upload (current)
- v0.2: WPF settings UI + system tray + SFTP
- v0.3: Installer + auto-start

## MVP Security Note
The Windows UI protects saved passwords with DPAPI for the current Windows user. Existing plain-text passwords from early MVP builds are migrated the next time settings are saved.

## Contributing
Pull requests welcome! Please open an issue first for major changes.

## License
MIT License — see LICENSE file.

---

**Stats data should flow freely.** 🏈📊
