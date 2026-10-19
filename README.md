# Core-Launcher

**Core-Launcher** is one app for playing and hosting with [Core V2](https://github.com/PongooDev/CoreV2). It starts [Core-Backend](https://github.com/PongooDev/Core-Backend), starts the game with Core's client and redirect DLLs, and hosts game servers for you, always keeping 2 server instances running so a free one is ready.

This repository does NOT include Fortnite game files, the backend, or the Core DLLs. The DLLs come from Core V2's latest GitHub release, or from a folder you build them into.

> Core-Launcher is an independent community project. It is not affiliated with, endorsed by, or supported by Epic Games.

[Join the Core Discord](https://discord.gg/auzEaKs7AS) · [Core V2](https://github.com/PongooDev/CoreV2) · [Core-Backend](https://github.com/PongooDev/Core-Backend) · [Report an issue](https://github.com/PongooDev/Core-Launcher/issues)

## Version support

| Fortnite version | Status |
| --- | --- |
| Cert CL-3532353 (UE 4.16) | In progress 🚧 |

Builds are added by folder: the launcher reads `Engine\Build\Build.version` and fills in that build's launch settings. Everything per build can be changed in Settings.

## Contents

- [Running](#running)
- [What it does](#what-it-does)
- [Settings](#settings)
- [Logs](#logs)
- [Building](#building)
- [Contributing](#contributing)
- [License](#license)

## Running

### Requirements
- Windows 10 or Windows 11, x64
- [Node.js](https://nodejs.org) 22 or newer, for Core-Backend
- A Core-Backend checkout with `npm install` run once
- A Fortnite build Core supports

### First run
1. Start `CoreLauncher.exe`.
2. Open **Settings**:
   - **Builds:** Add build… and pick the build folder (the one with `Engine` and `FortniteGame`).
   - **DLLs:** the latest Core V2 release, or a local folder such as `CoreV2\x64\Release`.
   - **Backend:** the Core-Backend folder.
3. Save. The backend starts, then the servers start one at a time.
4. Enter a player name and press **Play**.

`CoreLauncher.exe --data <folder>` keeps settings, downloads and logs in that folder instead of `%LOCALAPPDATA%\Core-Launcher`.

## What it does

- **Backend:** runs `node src/main.js --port <port> --data <folder>` and waits for `Listening on port <n>`. It restarts the backend if it exits, except when the port is taken or the options are bad. Ctrl+Break stops it cleanly. With "Start Core-Backend" off, the launcher uses one you started yourself.
- **Game processes:** the client and every server are created suspended. Each DLL's `LoadLibraryW` is queued on the main thread, so the DLLs load at process start, before `WinMain`; then the process resumes. Every process sits in a job object, so nothing outlives the launcher.
- **Servers:** keeps the configured number running and boots one at a time. A server that hasn't logged `Server ready` within the ready timeout (180 s) is restarted. A server that exits is restarted after 5 s, doubling up to 60 s while it keeps failing. Each ready server shows the UDP port it bound.
- **DLLs:** release DLLs are downloaded once per release and checked against the release's size and SHA-256. Local DLLs are copied (with their `.pdb`) before each start, so you can rebuild Core V2 while servers run and restarts pick up the new build.

## Settings

Settings are saved in `%LOCALAPPDATA%\Core-Launcher\settings.json`.

| Setting | Default | Purpose |
| --- | --- | --- |
| Player name | | Client login (`-AUTH_LOGIN`) |
| Builds | | Folder, executable, client and server arguments, `-httpproxy`, and which DLLs the client and servers load |
| DLLs | Latest `PongooDev/CoreV2` release | Or a local folder with `Core.dll`, `CoreClient.dll`, `CoreRedirect.dll` |
| Start Core-Backend | On | Off uses a backend that is already running |
| Backend folder | | Core-Backend checkout |
| Node.js | `node` | Node executable |
| Port | `5595` | `0` lets the backend pick a free port |
| Data folder | `<backend folder>\data` | The same folder `npm start` uses |
| Host servers | On | Autohost on or off |
| Servers to keep running | `2` | |
| Ready timeout | `180` s | `0` waits forever |
| Server login | `CoreServer{n}` | `{n}` is the server's number, so each server has its own account |

The launcher adds `-AUTH_LOGIN`, `-AUTH_PASSWORD` and `-AUTH_TYPE=epic` itself. It adds `-httpproxy=127.0.0.1:<port>` when the build uses it, and `-CoreBackend=http://127.0.0.1:<port>` for processes that load `CoreRedirect.dll`.

## Logs

Everything is under `%LOCALAPPDATA%\Core-Launcher\Logs` (**Open logs folder** in the launcher):

| File | Contents |
| --- | --- |
| `Launcher.log` | What the launcher did: command lines, PIDs, DLLs loaded, ready times, exit codes, restarts |
| `Backend.log` | Core-Backend's output |
| `Core\<Name>_log_<pid>.txt` | Each Core DLL's own log, moved here from the game folder when its process exits |

The previous run's `Launcher.log` and `Backend.log` are kept as `*.previous.log`. Include these files in issue reports.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build
dotnet test
dotnet publish src/Core.Launcher -c Release -o artifacts/publish
```

`dotnet publish` makes one self-contained `CoreLauncher.exe`. `dotnet test` also runs the real Core-Backend when `CORE_BACKEND_DIR` points at a checkout.

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
