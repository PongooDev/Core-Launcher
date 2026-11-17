# Core-Launcher: instructions for coding agents

Core-Launcher is the Windows launcher for Core V2 (C#, .NET 10, WPF). It installs and runs Core-Backend, downloads
the Core DLLs and server-assets paks, starts the Fortnite client and game servers with Core's DLLs loaded, and keeps
the servers running. `CoreServerHost.exe` is a console-only server host. Players get every change through the
launcher's auto-update, so **a change isn't delivered until a release is tagged**. This file is for any coding agent
working here (Freebuff, Codebuff, Claude Code or another). Read all of it before the first change.

Paths: `%USERPROFILE%` is the owner's Windows user folder. In PowerShell write it as `$env:USERPROFILE`, as the
command examples do.

## 0. Start of every session

1. Read `H:\CoreWork\HANDOFF.md` (local, shared by every agent, not committed). Don't redo or undo work listed in it.
2. Run the core MCP's `project_status` (if available) for the build in development and the supported range.
3. Work in your own git worktree under `H:\CoreWork` (section 5), never in the main checkout
   `%USERPROFILE%\Documents\GitHub\Core-Launcher`.
4. If you can't finish, update `H:\CoreWork\HANDOFF.md`: what is in progress, the branch or worktree, what was
   verified, what's next.

Per-build facts are in the read-only knowledge base
`%USERPROFILE%\.claude\projects\<the CoreV2 project folder>\memory\MEMORY.md` and the files it links. The folder name
is the CoreV2 checkout's path with `\` and `:` turned into `-`. Read it; **never edit it**.

## 1. Project rules

- **Release after every change.** After a launcher change is pushed, tag the next version and push the tag
  (section 4). Don't wait for the owner to test first. Docs-only changes (README, `AGENTS.md`, `docs/`, `.claude/`)
  don't need a release.
- **The game's files are the player's.** The launcher may only write what it already writes into a build (the server
  assets pak and sig, the config pak, the command-line whitelist patch). Static exe decryption stays one toggle,
  **off by default**, and must back up the original exe before writing; it is hidden and forced off for now.
- **Players' safety first.** Downloads are checked by size and SHA-256 before they are moved into place; the
  manifest is refused whole if any entry points outside Core-ServerAssets' releases. Keep it that way.
- **One build in development at a time**, shown everywhere as one range (`KnownBuilds.SupportSummary`).
- **No CoreClient.dll changes** come from here either: fix in the gameserver or backend.
- **Report honestly**: tests passing is static. Say what wasn't seen working on a real install.

## 2. Operating rules on the owner's PC

- **Never** run `CoreLauncher.exe` or `CoreServerHost.exe` on the owner's PC: they start a backend on port 5595 and
  game processes. Verify with the tests instead.
- **Never** launch a Fortnite client or run a full match. **Never** use port 5595 for tests. (Restarting the owner's
  5595 backend after a backend change is Core-Backend's procedure, not this repo's.)
- **Never** modify, copy, hard-link or overlay anything in `F:\Builds` or `D:\Fortnite Builds`. Tests use temp folders.
- **Never** kill a process you didn't start; stop what you started before you finish.
- **Never** print, copy or commit pak AES keys, tokens or signing material.
- Run any Python that reads downloaded or untrusted files with `python -I`.
- Work in worktrees under `H:\CoreWork`. Rebase onto `origin/main` before pushing. Stage only your own paths.
- **Pre-commit hooks:** `tools/githooks/pre-commit` runs on every commit (enabled per clone with
  `git config core.hooksPath tools/githooks`; this checkout has it on). It blocks pak AES keys, private keys, files
  over 5 MB, build output and dependency folders (`bin`, `obj`, `x64`, `.vs`, `node_modules`), binaries, game files
  and agent-local files. **Never** bypass it (`--no-verify`); fix what it flags.
- Close an issue only with a comment naming the fixing commit (and the release that ships it).

## 3. Repo map

| Path | What lives there |
| --- | --- |
| `src/Core.Launcher/` | the WPF app (`CoreLauncher.exe`): pages (`HomePage`, `ServersPage`, `SettingsPage`, `LogsPage`), view models, setup window |
| `src/Core.Launcher.Hosting/Builds/` | `KnownBuilds.cs` (every build Core knows, its support level and launch settings), `BuildDetector`, `BuildVersion`, `ExePatcher` (whitelist patch), `ExeDecryptor`/`BuildDecryptor` (off) |
| `src/Core.Launcher.Hosting/Backend/` | running or connecting to Core-Backend, auth, regions, local certificate trust |
| `src/Core.Launcher.Hosting/Components/` | downloads and updates: Core-Backend package, Node runtime, Core DLLs, `ServerAssets.cs` (manifest, pak + sig), `LauncherUpdater` |
| `src/Core.Launcher.Hosting/Processes/` | starting game processes suspended with the DLLs queued, command lines, `ServerConfig`, `ConfigPak` (6.20+ config pak), `PurchaseFlowConfig` |
| `src/Core.Launcher.Hosting/Servers/` | the server pool, plan, demand, backend sessions |
| `src/Core.Launcher.Hosting/Settings/`, `Logging/`, `Native/`, `Discord/`, `Dlls/` | settings, logs, Win32 interop, Rich Presence, DLL store |
| `src/Core.Server.Cli/` | `CoreServerHost.exe` |
| `tests/Core.Launcher.Tests/` | xUnit v3 tests (one file per area: `BuildSupportTests`, `ServerAssetsTests`, `ConfigPakTests`, ...) |
| `.github/workflows/build.yml`, `release.yml` | CI tests on push and PR; a `v*` tag runs tests, publishes both exes and creates the release |

## 4. Build, test and release commands

Prefer the core MCP's `launcher_tests` (if available). Fallback, from the worktree:

```
dotnet build
tests\Core.Launcher.Tests\bin\Debug\net10.0-windows\win-x64\Core.Launcher.Tests.exe
tests\Core.Launcher.Tests\bin\Debug\net10.0-windows\win-x64\Core.Launcher.Tests.exe -class Core.Launcher.Tests.BuildSupportTests
dotnet test --solution Core-Launcher.slnx -c Release        # what CI runs
```

- Warnings are errors (`TreatWarningsAsErrors`). Fix them; don't suppress them.
- `CORE_BACKEND_DIR` (a Core-Backend checkout) and `CORE_LAUNCHER_NETWORK_TESTS=1` turn on the slower integration
  tests; run them when you touch the backend host or downloads.

Release (after the change is on `origin/main`); details in `docs/agents/releasing.md`:

```
git fetch origin --tags
git tag --sort=-v:refname | Select-Object -First 1      # the latest, e.g. v1.0.69
git tag v1.0.70 origin/main
git push origin v1.0.70
gh run list -R PongooDev/Core-Launcher --workflow release.yml --limit 1
```

Patch (`v1.0.x`) for fixes and UI tweaks; minor for features. A suffix (`v1.1.0-beta`) makes a pre-release that the
updater skips.

## 5. How a change is done here

1. Read `HANDOFF.md`; run `project_status`.
2. Read the issue and its comments (`gh issue view <n> -R PongooDev/Core-Launcher --comments`), and the logs
   (`Launcher.log`, `Backend.log`, `Core\*_log_<pid>.txt`).
3. Worktree: `git -C $env:USERPROFILE\Documents\GitHub\Core-Launcher worktree add -b fix-<n> H:\CoreWork\la-<n> origin/main`.
4. Change the code, keeping old builds' behaviour unless the change is for them (gate by CL via `KnownBuilds`).
5. Add or update tests for the behaviour.
6. `dotnet build`, run the test exe (or `launcher_tests`).
7. Self-review: `.claude/skills/launcher-change-and-release` step list, and section 6.
8. Commit only your paths; `git fetch origin`, `git rebase origin/main`, rebuild and retest if needed, `git push origin HEAD:main`.
9. Tag and push the next version; check the Release run passed (`gh run watch <id> -R PongooDev/Core-Launcher`).
10. Comment on the issue with the commit and the release; close it if fixed.
11. Remove your worktree and branch.

## 6. Definition of done

- [ ] Builds with no warnings; every test passes (the test exe or `dotnet test`).
- [ ] New behaviour has tests; build-specific behaviour is pinned for its builds and for one build without it.
- [ ] Nothing new writes into a game build beyond what section 1 allows; downloads stay size and hash checked.
- [ ] The README says what users see when the change is user-facing (settings table, "What it does").
- [ ] Rebased, pushed, release tagged and its run green (code changes), issue commented with the release link.
- [ ] The report says what was only tested by unit tests and what a real install still has to show.

## 7. Commits, issues and hand-off

- Subject: `<area> (<#issue or repo#issue>): <what changed>`, e.g.
  `Store (Core-Backend #355): 6.01+ get the purchase page's NoRedist config in a pak`.
- **Trailer:** commits made by Freebuff or Codebuff end with the line `Agent: Freebuff` and must not use Claude's
  `Co-Authored-By` line. Each Freebuff commit is reviewed later, so say what was verified and how.
- Close an issue only with a comment naming the commit and the release. Reopen when the reporter says it still happens.
- Unfinished work goes in `H:\CoreWork\HANDOFF.md`, not in a pushed WIP branch.

**Pull requests: review, fix, merge.** Agents review every open PR, make it right and merge it themselves (the
owner's rule). Check it out in a worktree (`gh pr checkout`), review it against sections 1 and 6 (nothing new written
into game builds, checked downloads, build-specific behaviour gated by `KnownBuilds`, tests, no warnings), rework what
is wrong (push to the PR branch when maintainers can edit it; otherwise merge first and fix on main right after, in
the same session), build and run the tests, merge with `gh pr merge <n> --merge` (the repo's usual merge commit),
comment on the PR with what was changed and why, then **tag a release** (section 4). Reject a PR only when it adds
something the project has ruled out (CoreClient changes without the owner's OK, risky features on by default,
writing into game builds), with a comment. Steps: the `review-and-merge-pr` skill.

## 8. Mistakes made here before (don't repeat them)

1. **Config the build can't read.** From 6.20 the client ignores loose `.ini` files except `GameUserSettings.ini`,
   so the purchase page's NoRedist config did nothing; it now ships in a config pak (977ccef).
2. **Config that kills a newer build.** The NoRedist `Protocol=http` for the purchase page made 7.10's login check
   call `RequestExit` (1f79bd9 stopped writing it from 7.00). **Do** check each config key against the newest
   build's code before writing it for every build.
3. **A pak without its sig.** Signed builds (3.0) refuse to start with a pak that has no `.sig`; the launcher and the
   manifest had to learn to install both together (86be21d). **Do** treat pak and sig as one unit.
4. **Pushed WIP.** `a293558 WIP: ...` and the `origin/import-rebuild` branch were left on GitHub. **Don't** push WIP
   commits or branches; use `HANDOFF.md`.
5. **Unreleased changes.** A pushed change without a tag never reached the owner, who tests through auto-update, and
   a release held back "until the owner tests" stalled for a night. **Do** tag right after pushing a code change.
6. **Risky features on by default.** Static exe decryption was hidden and forced off (1b533df) because it isn't
   proven. **Do** ship unproven features off by default, behind one setting.
7. **Packer import table.** Clearing the packer's private import slots made the loader fill none of them; baking
   resolved addresses was unstable. Leave that code alone unless the owner asks.

## 9. Tools: MCP servers

- **core MCP** (if available): `project_status`, `launcher_tests`. Prefer them over hand-typed commands.
- **IDA MCPs** (`ida-13337` to `ida-13341`) and the **fmodel MCP**: read-only. Run `project_status` first to learn
  which port holds which build. **Never** patch, rename, define or `idb_save`.

## 10. Facts that change: check, don't assume

The build in development, the supported range and the latest release tag change often; one build is in development
at a time. Check `KnownBuilds.cs`, the README status table, `git tag`, and `project_status`.

## 11. Deeper docs

| Doc | Read it when |
| --- | --- |
| [docs/agents/knownbuilds.md](docs/agents/knownbuilds.md) | adding a build, promoting the in-development build, build-specific launch settings |
| [docs/agents/releasing.md](docs/agents/releasing.md) | tagging a release, checking the run, what counts as a change |

Skills in `.claude/skills/`: `launcher-change-and-release`, `review-and-merge-pr`.

Related repos: [CoreV2](https://github.com/PongooDev/CoreV2) (the DLLs; its `docs/agents/new-build-bringup.md` is the
full new-build routine), [Core-Backend](https://github.com/PongooDev/Core-Backend),
[Core-ServerAssets](https://github.com/PongooDev/Core-ServerAssets) (the manifest the launcher reads).
