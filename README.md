# Core-Launcher

**Core-Launcher** is one app for playing and hosting with [Core V2](https://github.com/PongooDev/CoreV2). It installs and starts [Core-Backend](https://github.com/PongooDev/Core-Backend), starts the game with Core's client and redirect DLLs, and hosts game servers for you, always keeping 2 server instances running so a free one is ready.

This repository does NOT include Fortnite game files. Core-Backend, a Node.js runtime for it, and the Core DLLs are downloaded by the launcher.

> Core-Launcher is an independent community project. It is not affiliated with, endorsed by, or supported by Epic Games.

[Download](https://github.com/PongooDev/Core-Launcher/releases/latest) · [Join the Core Discord](https://discord.gg/auzEaKs7AS) · [Core V2](https://github.com/PongooDev/CoreV2) · [Core-Backend](https://github.com/PongooDev/Core-Backend) · [Report an issue](https://github.com/PongooDev/Core-Launcher/issues)

## Version support

| Status | Fortnite versions |
| --- | --- |
| Supported ✅ | 1.1 – 6.21 |
| In progress 🚧 | 6.22 |

Core moves forward one build at a time and skips builds nobody has a copy of, so the supported builds are always one range: every build from the first version to the last one listed. Fortnite 3.4 and 4.3 are not supported: nobody has a copy of them, so the range skips them. The build Core V2 is being developed on ([Version support](https://github.com/PongooDev/CoreV2#version-support)) is added here as **in progress** once Core runs on it.

<details>
<summary>Every build by changelist</summary>

| Fortnite version | Build | Status |
| --- | --- | --- |
| 1.1 | Cert CL-3532353 (UE 4.16) | Supported ✅ |
| 1.2 | Release-Cert CL-3541083 (UE 4.16) | Supported ✅ |
| 1.7.2 | Release-Cert CL-3700114 (UE 4.16) | Supported ✅ |
| 1.8 | Release-Live CL-3724489 (UE 4.16) | Supported ✅ |
| 1.8.1 | Release-Cert CL-3729133 (UE 4.16) | Supported ✅ |
| 1.8.2 | Release-Cert CL-3741772 (UE 4.16) | Supported ✅ |
| 1.9 | Release-Live CL-3757339 (UE 4.16) | Supported ✅ |
| 1.9.1 | Release-Cert CL-3775276 (UE 4.16) | Supported ✅ |
| 1.10 | Release-Cert CL-3790078 (UE 4.19) | Supported ✅ |
| 1.11 | Release-Cert CL-3807424 (UE 4.19) | Supported ✅ |
| 2.1 | Release-Cert CL-3825894 (UE 4.19) | Supported ✅ |
| 2.2 | Release-Cert CL-3841827 (UE 4.19) | Supported ✅ |
| 2.3 | Release-Cert CL-3847564 (UE 4.19) | Supported ✅ |
| 2.4 | Release-Cert CL-3858292 (UE 4.19) | Supported ✅ |
| 2.4.2 | Release-Next CL-3870737 (UE 4.19) | Supported ✅ |
| 2.5 | Release-2.5 CL-3889387 (UE 4.20) | Supported ✅ |
| 3.0 | Release-3.0.0 CL-3901517 (UE 4.20) | Supported ✅ |
| 3.1 | Release-3.1 CL-3915963 (UE 4.20) | Supported ✅ |
| 3.1.1 | Release-3.1 CL-3917250 (UE 4.20) | Supported ✅ |
| 3.2 | Release-3.2 CL-3935073 (UE 4.20) | Supported ✅ |
| 3.3 | Release-3.3 CL-3942182 (UE 4.20) | Supported ✅ |
| 3.5.1 | Release-3.5 CL-4000805 (UE 4.20) | Supported ✅ |
| 3.5.2 | Release-3.5 CL-4008490 (UE 4.20) | Supported ✅ |
| 3.6 | Release-3.6 CL-4019403 (UE 4.20) | Supported ✅ |
| 4.0 | Release-4.0 CL-4039451 (UE 4.20) | Supported ✅ |
| 4.1 | Release-4.1 CL-4053532 (UE 4.20) | Supported ✅ |
| 4.2 | Release-4.2 CL-4072250 (UE 4.20) | Supported ✅ |
| 4.4.0 | Release-4.4 CL-4117433 (UE 4.20) | Supported ✅ |
| 4.4.1 | Release-4.4 CL-4127312 (UE 4.20) | Supported ✅ |
| 4.4.2 | Release-4.4.x CL-4132537 (UE 4.20) | Supported ✅ |
| 4.5 | Release-4.5 CL-4159770 (UE 4.20) | Supported ✅ |
| 4.5 | Release-4.5 CL-4166199 (UE 4.20) | Supported ✅ |
| 5.0 | Release-5.0 CL-4204761 (UE 4.21) | Supported ✅ |
| 5.0 | Release-5.0 CL-4214610 (UE 4.21) | Supported ✅ |
| 5.10 | Release-5.10 CL-4225813 (UE 4.21) | Supported ✅ |
| 5.10 | Release-5.10 CL-4240749 (UE 4.21) | Supported ✅ |
| 5.21 | Release-5.21 CL-4288479 (UE 4.21) | Supported ✅ |
| 5.30 | Release-5.30 CL-4305896 (UE 4.21) | Supported ✅ |
| 5.40 | Release-5.40 CL-4352937 (UE 4.21) | Supported ✅ |
| 5.41 | Release-5.41 CL-4363240 (UE 4.21) | Supported ✅ |
| 6.00 | Release-6.00 CL-4395664 (UE 4.21) | Supported ✅ |
| 6.00 | Release-6.00 CL-4402180 (UE 4.21) | Supported ✅ |
| 6.01 | Release-6.01 CL-4417689 (UE 4.21) | Supported ✅ |
| 6.01 | Release-6.01 CL-4424678 (UE 4.21) | Supported ✅ |
| 6.02 | Release-6.02 CL-4442095 (UE 4.21) | Supported ✅ |
| 6.02 | Release-6.02 CL-4461277 (UE 4.21) | Supported ✅ |
| 6.10 | Release-6.10 CL-4464155 (UE 4.21) | Supported ✅ |
| 6.10 | Release-6.10 CL-4476098 (UE 4.21) | Supported ✅ |
| 6.10 | Release-6.10 CL-4480234 (UE 4.21) | Supported ✅ |
| 6.20 | Release-6.20 CL-4497486 (UE 4.21) | Supported ✅ |
| 6.21 | Release-6.21 CL-4526925 (UE 4.21) | Supported ✅ |
| 6.22 | Release-6.22 CL-4543176 (UE 4.21) | In progress 🚧 |

</details>

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
3. Under **Whose backend**, keep **Run my own backend**, or pick **Connect to a friend's backend** and enter its address (see [Play with friends](#play-with-friends)): the launcher checks that it answers, and then installs and runs no backend of its own.
4. Press **Install**. The launcher downloads Node.js and Core-Backend (only for your own backend) and the Core DLLs, then **Continue**.
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

1. In first-run setup pick **Connect to a friend's backend** and enter the host's address, or later: Settings → Backend → **A friend's backend**. Enter a name and the host's address, press **Test connection**, then
   **Save to list** and **Save**. (Or pick a saved friend under **Play on** on Home; **Join a friend** goes to the same
   settings.)
2. Press **Play**. You log in to the host's backend with your player name (a new account there the first time), and
   matchmaking sends you to the host's servers.

While you play on a friend's backend the launcher runs no backend, and the admin panel stays on the host's PC. You need
the same Fortnite build as the host. Choose **My own backend** to go back.

**A backend behind an https domain** (a reverse proxy such as Caddy, nginx or a Cloudflare tunnel with a real
certificate in front of Core-Backend): enter `https://your.domain` (port 443 unless you add one). The certificate is
checked as a browser would, so it has to be valid for that name. The game then reaches the backend through CoreRedirect
alone (no `-httpproxy`), and matchmaking and notifications use `wss://`. The proxy has to pass WebSocket upgrades and
`X-Forwarded-Proto: https` on to the backend; XMPP (friends, parties, chat) is plain TCP that no HTTP proxy carries, so
the backend's own port stays reachable at its public address for it.

**Hosting servers for a friend's backend** (more servers, or servers in another region): with **Host servers** on, a
launcher on a friend's backend hosts its servers for that backend (Core-Launcher #6):

1. The host turns on remote access (above) and, in the admin panel's **Settings** → **Trusted Hosting IPs**, adds your
   PC's IP: your public IP over the internet, or your network/VPN address on theirs. Until then their backend refuses
   your servers (403), and the Servers page says so.
2. Your servers log in to the host's backend as `CoreServer-<your PC name>-{n}` (Settings → Servers → **PC name on a
   friend's backend**; empty uses your computer's name), so they don't share accounts with the host's own
   `CoreServer{n}`, and register with the host's region: the first of the **Server Regions** ticked on the host's
   admin panel (Matchmaking), or NA when none is. To host another region, name it in the build's server arguments
   (Settings → Builds, e.g. `-McpRegion=EU`) and have the host tick it among the Server Regions.
3. Players are sent to your PC's IP: for players on the internet, forward UDP 7777 and 15000 and up to your PC.

This needs a Core-Backend with Trusted Hosting IPs and `CoreServer-<PC>-{n}` logins (Core-Backend #125 and later;
the launcher's managed backend updates itself).

Core doesn't check passwords, so anyone who can reach a host's backend can log in with any name: hosts should only
share the address with people they trust.

## What it does

- **Backend:** installs Core-Backend from its latest release (or its main branch until it has one) with a private Node.js, and runs it on port 5595. It restarts the backend if it exits, and stops it cleanly when you close the launcher. Accounts are kept in `Backend\data` across updates.
- **Game processes:** the client and every server are created suspended. Each DLL's `LoadLibraryW` is queued on the main thread, so the DLLs load at process start, before `WinMain`; then the process resumes. Every process sits in a job object, so nothing outlives the launcher.
- **Servers:** keeps the configured number running and boots one at a time. A server that hasn't logged `Server ready` within the ready timeout (180 s) is restarted. After `Server ready` it still logs in and registers its matchmaking session (usually 35–75 s later), so it shows as *Registering* and only counts as ready, and the next server only boots, once Core-Backend lists that session (`/core/api/sessions`); a backend without that listing counts it ready at `Server ready`, and one that doesn't list it within 180 s counts it ready anyway (both are logged). A server that exits is restarted after 5 s, doubling up to 60 s while it keeps failing. Each ready server shows the UDP port it bound.
- **Server assets:** PC builds from 1.10 on lack some assets only a server uses. Before a build's first server starts, the launcher reads [Core-ServerAssets](https://github.com/PongooDev/Core-ServerAssets)' `manifest.json` (15 s timeout; the last good copy is kept in `ServerAssets\manifest.json`). When it lists the build's changelist, the launcher downloads that pak to a temporary file, checks its size and SHA-256, and only then puts it in the build's `FortniteGame\Content\Paks` as `pakchunkCoreServerAssets-WindowsClient_P.pak` (the `_P` makes it mount above the game's paks). Builds that sign their paks (3.0, for one) refuse to start with a pak that has no `.sig` next to it, so when the manifest gives the pak a `sig`, the launcher installs it alongside as `pakchunkCoreServerAssets-WindowsClient_P.sig`: both files are downloaded and checked before either is moved into place, and they are replaced and removed together, so a pak is never left without its sig. Without a `sig` in the manifest, no sig is installed. A manifest with a `sig` is schema 2, which launchers before 1.0.15 don't read, so they install nothing rather than a pak without its sig. A pak (and sig) that is already installed and matches is left alone. Core's pak and sig are removed when the manifest no longer lists the build or **Download server assets for hosted servers** is turned off. The game's own paks are never touched. If GitHub can't be reached, the server log says so and the servers start anyway. Players don't need the pak.

## Updates

Each time it starts, and when you press **Check for updates** (Settings → About), the launcher checks for:

| Component | From | Checked against |
| --- | --- | --- |
| Core Launcher | [this repository's latest release](https://github.com/PongooDev/Core-Launcher/releases/latest) | release size and SHA-256 |
| Core DLLs | [Core V2's latest release](https://github.com/PongooDev/CoreV2/releases/latest) | release size and SHA-256 |
| Core-Backend | Core-Backend's latest release, or its main branch until it has one | |
| Server assets pak (and its sig) | [Core-ServerAssets](https://github.com/PongooDev/Core-ServerAssets)' manifest, before a build's first server starts | the manifest's size and SHA-256 |
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
| Play on | My own backend | Or a friend's backend: a name and an address (`host` or `host:port`, port 5595 by default, or `https://host` behind an https reverse proxy), with a saved list |
| Port | `5595` | `0` lets the backend pick a free port |
| Data folder | `Backend\data` | Your own folder uses its `data` folder, the one `npm start` uses |
| Host servers | On | Autohost on or off |
| Servers to keep running | `2` | Save the World's on a build with Battle Royale |
| Battle Royale servers | `2` | On a build with Battle Royale (1.7.2 on), on top of the servers above; each match takes a server of its own |
| Extra servers (per build, Builds) | `0` | Servers for that build on top of the selected build's, whichever build is selected, so others can play it while you play another: Battle Royale on builds that have it, otherwise Save the World |
| Switch idle servers to the builds players are on | Off | A server empty for 30 s restarts on the build of players who find no free server (one of your builds; needs a Core-Backend with `/core/api/demand`) |
| Ready timeout | `180` s | `0` waits forever |
| Download server assets for hosted servers | On | Installs the build's server assets pak before its servers start ([What it does](#what-it-does)); off removes it |
| Server login | `CoreServer{n}` | `{n}` is the server's number, so each server has its own account |

The launcher adds `-AUTH_LOGIN`, `-AUTH_PASSWORD` and `-AUTH_TYPE=epic` itself. It adds `-httpproxy=127.0.0.1:<port>` when the build uses it, `-CoreBackend=http://127.0.0.1:<port>` for processes that load `CoreRedirect.dll` (both with the friend's address instead when you play on a friend's backend), `-McpRegion=` for servers (the region they host: the first of the backend's **Server Regions**, on its admin panel's Matchmaking page, or `NA` when none is ticked; a friend's backend gives its own, and a build whose server arguments name a region other than NA keeps it), and `-nullrhi -unattended` for servers (no renderer, and engine message boxes are logged instead of waiting for a click, as dedicated servers run). The shipping exe drops any switch missing from its command line whitelist, `-nullrhi` included, so before a build's first server starts the launcher patches its exe the way Reboot Launcher does: four client-only switches in the whitelist become `-log -nosplash -nosound -nullrhi`. Core's server makes the same change in memory, so an exe the launcher can't write to still works. Battle Royale servers also get `-CoreMode=BattleRoyale`: Core takes the map and playlist from the build itself and registers the server with Core-Backend's matchmaking.

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
- **Dedicated CLI Server Hoster (`CoreServerHost.exe`)**: A lightweight, standalone console application for hosting dedicated game servers without the GUI. Features a clean, monochrome terminal dashboard (system and process RAM/CPU monitoring, direct clickable admin panel link, server slot status table). It connects to an existing backend (local or friend's) and configures via `settings.ini` in its folder:
  ```
  CoreServerHost.exe
  CoreServerHost.exe --servers 2 --br-servers 2 --backend http://127.0.0.1:5595 --login MyServer_{n}
  ```
  Configure `settings.ini` settings: `BuildPath`, `BackendUrl`, `Region`, `StandardServers`, `BattleRoyaleServers`, `ServerLogin` (defaults to `CoreServer{n}`), `PcName`, `ReadyTimeout`, `AllowUnsupportedBuilds`, and `AutoUpdates`.

  `AutoUpdates=on|off` (default `on`): before starting servers the hoster checks this repository's latest release and, when a newer one exists, downloads `CoreServerHost.exe` (size and SHA-256 checked), replaces itself and restarts with the same arguments. With `off` it only logs that an update is available. If GitHub can't be reached or the update fails, the servers start anyway.

  Interactive keyboard shortcuts while running:
  - `n`: Create a new server (pick Save The World or Battle Royale)
  - `d`: Remove a server (pick which active slot to stop and remove)
  - `c`: Open `settings.ini` in Notepad
  - `a`: Open Admin Panel dashboard in default browser
  - `l`: Open logs folder in File Explorer
  - `Ctrl+C`: Stop all servers and exit cleanly

### Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet build
dotnet test
dotnet publish src/Core.Launcher -c Release -o artifacts/publish
dotnet publish src/Core.Server.Cli -c Release -o artifacts/publish
```

`dotnet publish` makes self-contained `CoreLauncher.exe` and `CoreServerHost.exe`. `dotnet test` also runs the real Core-Backend when `CORE_BACKEND_DIR` points at a checkout, and the full download-and-install path when `CORE_LAUNCHER_NETWORK_TESTS=1`.

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
