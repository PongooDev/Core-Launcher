# Core-Launcher

**Core-Launcher** is one app for playing and hosting with [Core V2](https://github.com/PongooDev/CoreV2). It installs and starts [Core-Backend](https://github.com/PongooDev/Core-Backend), starts the game with Core's client and redirect DLLs, and hosts game servers for you, always keeping 2 server instances running so a free one is ready.

This repository does NOT include Fortnite game files. Core-Backend, a Node.js runtime for it, and the Core DLLs are downloaded by the launcher.

> Core-Launcher is an independent community project. It is not affiliated with, endorsed by, or supported by Epic Games.

[Download](https://github.com/PongooDev/Core-Launcher/releases/latest) · [Join the Core Discord](https://discord.gg/auzEaKs7AS) · [Core V2](https://github.com/PongooDev/CoreV2) · [Core-Backend](https://github.com/PongooDev/Core-Backend) · [Report an issue](https://github.com/PongooDev/Core-Launcher/issues)

## Version support

| Fortnite version | Status |
| --- | --- |
| Cert CL-3532353 (UE 4.16) | In progress 🚧 |

## Contents

- [Getting started](#getting-started)
- [What it does](#what-it-does)
- [Updates](#updates)
- [Settings](#settings)
- [Logs](#logs)
- [For developers](#for-developers)
- [Contributing](#contributing)
- [License](#license)

## Getting started

### Requirements
- Windows 10 or Windows 11, x64
- A Fortnite build Core supports

### Setup
1. Download `CoreLauncher.exe` from the [latest release](https://github.com/PongooDev/Core-Launcher/releases/latest) and run it. Windows may warn that it is from an unknown publisher (the exe isn't code-signed): choose **More info → Run anyway**.
2. Enter a player name and pick your Fortnite build folder (the one with `Engine` and `FortniteGame`).
3. Press **Install**. The launcher downloads Node.js, Core-Backend and the Core DLLs, then **Continue**.
4. Press **Play**.

Everything goes in `%LOCALAPPDATA%\Core-Launcher`; nothing is installed system-wide.

## What it does

- **Backend:** installs Core-Backend from its latest release (or its main branch until it has one) with a private Node.js, and runs it on port 5595. It restarts the backend if it exits, and stops it cleanly when you close the launcher. Accounts are kept in `Backend\data` across updates.
- **Game processes:** the client and every server are created suspended. Each DLL's `LoadLibraryW` is queued on the main thread, so the DLLs load at process start, before `WinMain`; then the process resumes. Every process sits in a job object, so nothing outlives the launcher.
- **Servers:** keeps the configured number running and boots one at a time. A server that hasn't logged `Server ready` within the ready timeout (180 s) is restarted. A server that exits is restarted after 5 s, doubling up to 60 s while it keeps failing. Each ready server shows the UDP port it bound.

## Updates

Each time it starts, and when you press **Check for updates**, the launcher checks for:

| Component | From | Checked against |
| --- | --- | --- |
| Core Launcher | [this repository's latest release](https://github.com/PongooDev/Core-Launcher/releases/latest) | release size and SHA-256 |
| Core DLLs | [Core V2's latest release](https://github.com/PongooDev/CoreV2/releases/latest) | release size and SHA-256 |
| Core-Backend | Core-Backend's latest release, or its main branch until it has one | |
| Node.js | the newest LTS on [nodejs.org](https://nodejs.org) (installed once) | nodejs.org's `SHASUMS256.txt` |

A launcher update shows a banner; **Update** replaces `CoreLauncher.exe` and restarts it. A new Core-Backend is used from the backend's next start, and new DLLs from each game process's next start.

## Settings

Settings are saved in `%LOCALAPPDATA%\Core-Launcher\settings.json`.

| Setting | Default | Purpose |
| --- | --- | --- |
| Player name | | Client login (`-AUTH_LOGIN`) |
| Builds | | Folder, executable, client and server arguments, `-httpproxy`, and which DLLs the client and servers load |
| DLLs | Latest `PongooDev/CoreV2` release | Or a local folder with `Core.dll`, `CoreClient.dll`, `CoreRedirect.dll` |
| Backend | Installed by the launcher | Or your own Core-Backend folder, or one that is already running |
| Port | `5595` | `0` lets the backend pick a free port |
| Data folder | `Backend\data` | Your own folder uses its `data` folder, the one `npm start` uses |
| Host servers | On | Autohost on or off |
| Servers to keep running | `2` | |
| Ready timeout | `180` s | `0` waits forever |
| Server login | `CoreServer{n}` | `{n}` is the server's number, so each server has its own account |

The launcher adds `-AUTH_LOGIN`, `-AUTH_PASSWORD` and `-AUTH_TYPE=epic` itself. It adds `-httpproxy=127.0.0.1:<port>` when the build uses it, `-CoreBackend=http://127.0.0.1:<port>` for processes that load `CoreRedirect.dll`, and `-unattended` for servers (as dedicated servers run: engine message boxes are logged instead of waiting for a click).

## Logs

Everything is under `%LOCALAPPDATA%\Core-Launcher\Logs` (**Open logs folder** in the launcher):

| File | Contents |
| --- | --- |
| `Launcher.log` | What the launcher did: downloads, command lines, PIDs, DLLs loaded, ready times, exit codes, restarts |
| `Backend.log` | Core-Backend's output |
| `Core\<Name>_log_<pid>.txt` | Each Core DLL's own log, moved here from the game folder when its process exits |

The previous run's `Launcher.log` and `Backend.log` are kept as `*.previous.log`. Include these files in issue reports.

## For developers

- In setup, **Advanced** (or later, Settings) points the launcher at your own Core-Backend checkout and your own DLL folder, such as `CoreV2\x64\Release`. Local DLLs are copied (with their `.pdb`) before each start, so you can rebuild Core V2 while servers run, and restarts pick up the new build.
- `CoreLauncher.exe --data <folder>` keeps settings, downloads and logs in that folder instead of `%LOCALAPPDATA%\Core-Launcher`.

### Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build
dotnet test
dotnet publish src/Core.Launcher -c Release -o artifacts/publish
```

`dotnet publish` makes one self-contained `CoreLauncher.exe`. `dotnet test` also runs the real Core-Backend when `CORE_BACKEND_DIR` points at a checkout, and the full download-and-install path when `CORE_LAUNCHER_NETWORK_TESTS=1`.

### Releasing

Push a version tag; the Release workflow tests, publishes and creates the GitHub release with `CoreLauncher.exe`:

```
git tag v0.2.0
git push origin v0.2.0
```

A tag with a suffix (`v0.2.0-beta`) becomes a pre-release, which the launcher's update check skips.

## Contributing

Contributions are welcome, whether it is code or an issue report.

### Issue reports

Include all of the following in a report if possible:

1. Fortnite version and CL
2. What you did and what happened
3. `Launcher.log`, `Backend.log` and the `Core` logs for that run
4. Core-Launcher, Core V2 and Core-Backend versions or commits

### Code

1. Keep unrelated fixes in separate pull requests.
2. Keep your code clean and readable.
3. Test your code and include the builds you tested it on.

## Credits

- **PongooDev**: project creator and primary maintainer
- [Core-Launcher contributors](https://github.com/PongooDev/Core-Launcher/graphs/contributors)

## License

Core-Launcher is licensed under the [GNU General Public License v3.0](LICENSE).
