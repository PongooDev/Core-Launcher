# Core-Launcher

**Core-Launcher** is one app for playing and hosting with [Core V2](https://github.com/PongooDev/CoreV2). It installs and starts [Core-Backend](https://github.com/PongooDev/Core-Backend), starts the game with Core's client and redirect DLLs, and hosts game servers for you, always keeping 2 server instances running so a free one is ready.

This repository does NOT include Fortnite game files. Core-Backend, a Node.js runtime for it, and the Core DLLs are downloaded by the launcher.

> Core-Launcher is an independent community project. It is not affiliated with, endorsed by, or supported by Epic Games.

[Download](https://github.com/PongooDev/Core-Launcher/releases/latest) · [Join the Core Discord](https://discord.gg/auzEaKs7AS) · [Core V2](https://github.com/PongooDev/CoreV2) · [Core-Backend](https://github.com/PongooDev/Core-Backend) · [Report an issue](https://github.com/PongooDev/Core-Launcher/issues)

## Version support

| Fortnite version | Build | Status |
| --- | --- | --- |
| 1.1 | Cert CL-3532353 (UE 4.16) | Supported ✅ |
| 1.2 | Release-Cert CL-3541083 (UE 4.16) | In progress 🚧 |

**Supported** builds are the ones Core is tested on. **In progress** builds play and host the same way (same Core DLLs, `-httpproxy` and server arguments), without any switch, but Core's support for them isn't finished, so some things may not work yet: Home shows "support in progress" next to the build, and each client and server log starts with a note saying so.

Other builds are turned away unless you turn on **Allow unsupported builds** (Settings → Builds): they then get the same Core DLLs, `-httpproxy` and server arguments as 1.1 and can play and host, but Core is only tested on the builds above, so they may not work.

## Contents

- [Getting started](#getting-started)
- [Play with friends](#play-with-friends)
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

Once a client is running, **Launch another client** on Home starts one more with a username you choose (same build and DLLs, its own login and log); **Running clients** lists them with View log and Close.

The sidebar's **Admin panel** opens Core-Backend's admin panel in your browser (while the backend is running).

**Home** says in one line whether you can play, and when something is wrong it offers the fix (for example "Use the running backend" when another program has the backend's port). **Servers** shows each hosted server and lets you change how many run, **Logs** has every log with Copy and Open logs folder, and **Settings** holds the rest.

Everything goes in `%LOCALAPPDATA%\Core-Launcher`; nothing is installed system-wide.

## Play with friends

One of you hosts: their launcher runs Core-Backend and the gameservers as usual. Everyone else joins the host's
backend instead of running their own.

**The host:**

1. Open the **Admin panel** (sidebar) → **Friends** and press **Turn on remote access**. Core-Backend only accepts
   connections from your own PC until you do. Allow Node.js through Windows Firewall if it asks, and Fortnite when your
   servers start.
2. For friends on the internet, forward these ports on your router to your PC (same numbers on both sides):
   TCP `5595` (the backend), UDP `7777` and up (one game port per server) and UDP `15000` and up (one beacon port per
   server). The Friends page lists the exact ports your running servers use. Friends on your network or on a VPN
   (Tailscale, ZeroTier, Hamachi, Radmin) need no forwarding.
3. Give your friends your address: your public IP for the internet, or the network/VPN address the Friends page shows,
   e.g. `203.0.113.7` (add `:port` if your backend doesn't use 5595).

**A friend:**

1. Settings → Backend → **A friend's backend**. Enter a name and the host's address, press **Test connection**, then
   **Save to list** and **Save**. (Or pick a saved friend under **Play on** on Home; **Join a friend** goes to the same
   settings.)
2. Press **Play**. You log in to the host's backend with your player name (a new account there the first time), and
   matchmaking sends you to the host's servers.

While you play on a friend's backend the launcher runs no backend and hosts no servers (they would log in with the same
server accounts as the host's), and the admin panel stays on the host's PC. You need the same Fortnite build as the
host. Choose **My own backend** to go back.

Core doesn't check passwords, so anyone who can reach a host's backend can log in with any name: hosts should only
share the address with people they trust.

## What it does

- **Backend:** installs Core-Backend from its latest release (or its main branch until it has one) with a private Node.js, and runs it on port 5595. It restarts the backend if it exits, and stops it cleanly when you close the launcher. Accounts are kept in `Backend\data` across updates.
- **Game processes:** the client and every server are created suspended. Each DLL's `LoadLibraryW` is queued on the main thread, so the DLLs load at process start, before `WinMain`; then the process resumes. Every process sits in a job object, so nothing outlives the launcher.
- **Servers:** keeps the configured number running and boots one at a time. A server that hasn't logged `Server ready` within the ready timeout (180 s) is restarted. After `Server ready` it still logs in and registers its matchmaking session (usually 35–75 s later), so it shows as *Registering* and only counts as ready, and the next server only boots, once Core-Backend lists that session (`/core/api/sessions`); a backend without that listing counts it ready at `Server ready`, and one that doesn't list it within 180 s counts it ready anyway (both are logged). A server that exits is restarted after 5 s, doubling up to 60 s while it keeps failing. Each ready server shows the UDP port it bound.

## Updates

Each time it starts, and when you press **Check for updates** (Settings → About), the launcher checks for:

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
| Allow unsupported builds | Off | Gives builds Core doesn't support Core's defaults and lets them play and host (with a warning); builds whose Core options you set by hand are left alone. Supported and in-progress builds don't need it |
| DLLs | Latest `PongooDev/CoreV2` release | Or a local folder with `Core.dll`, `CoreClient.dll`, `CoreRedirect.dll` |
| Backend | Installed by the launcher | Or your own Core-Backend folder, or one that is already running |
| Play on | My own backend | Or a friend's backend: a name and an address (`host` or `host:port`, port 5595 by default), with a saved list |
| Port | `5595` | `0` lets the backend pick a free port |
| Data folder | `Backend\data` | Your own folder uses its `data` folder, the one `npm start` uses |
| Host servers | On | Autohost on or off |
| Servers to keep running | `2` | |
| Ready timeout | `180` s | `0` waits forever |
| Server login | `CoreServer{n}` | `{n}` is the server's number, so each server has its own account |

The launcher adds `-AUTH_LOGIN`, `-AUTH_PASSWORD` and `-AUTH_TYPE=epic` itself. It adds `-httpproxy=127.0.0.1:<port>` when the build uses it, `-CoreBackend=http://127.0.0.1:<port>` for processes that load `CoreRedirect.dll` (both with the friend's address instead when you play on a friend's backend), and `-nullrhi -unattended` for servers (no renderer, and engine message boxes are logged instead of waiting for a click, as dedicated servers run).

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
