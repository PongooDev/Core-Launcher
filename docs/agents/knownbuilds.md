# KnownBuilds: the builds the launcher knows

`src/Core.Launcher.Hosting/Builds/KnownBuilds.cs` is the launcher's list of every Fortnite build Core knows, by
changelist (CL), with its support level and launch settings. The launcher detects a build's CL from its exe
(`BuildDetector`, `BuildVersion`), then asks `KnownBuilds` what to do with it.

## What is in it

- `BuildSupport`: `Unsupported` (turned away unless "Allow unsupported builds" is on), `InProgress` (plays and hosts,
  marked "support in progress"), `Full`.
- One `public const int FortniteXY = <CL>;` per build, with a factual `<summary>`: branch, UE version, season,
  release date, the update's headline features and its patch-notes link. Two CLs of one version get two consts
  (e.g. `Fortnite610Earlier`).
- `Builds`: the `KnownBuild` records, **oldest first**: `new(<const>, "<version>", "Fortnite <version> (CL-<CL>)",
  BuildSupport.<level>, [<former names>], BattleRoyale: true, Creative: true)`. `BattleRoyale` from 1.7.2;
  `Creative` from 7.00.
- `SupportSummary`, `SupportedNames`, `InProgressNames`: the one-range text every message uses
  ("Fortnite 1.1 to 7.10, and support for Fortnite 7.20 is in progress").
- Launch settings shared by every known build (`WithCore`), regions, and the messages for unsupported builds.

## Promoting a build (new-build routine)

Only one build is in development at a time. When the owner starts build X (the full routine is CoreV2's
`docs/agents/new-build-bringup.md`):

1. Add `FortniteXY = <CL>` with its summary (from the build's version string and the patch notes).
2. Add its `Builds` entry with `BuildSupport.InProgress` at the end; change the previous in-development entry to
   `BuildSupport.Full`.
3. Tests (the last promotion, bf25360, touched these):
   - `BuildSupportTests`: `InProgressNames` and `SupportSummary` strings, and the unsupported-build message;
   - `CommandLineTests`: the in-development build's `SupportOf`, `HasBattleRoyale`, `HasCreative`;
   - `BuildDetectorTests`, `SettingsAndBuildTests`: any assertion naming the newest build.
   The "unsupported" example is 3.4 (CL-3968866), which Core never supports; keep it.
4. `README.md`: the status table's ranges and the per-build `<details>` row.
5. Build, run all tests, commit, push, **tag a release** (`releasing.md`).
6. CoreV2 and Core-Backend READMEs and issue-form dropdowns change in the same routine (their own repos).

## Build-specific launch behaviour

Gate anything build-specific on the CL through `KnownBuilds` (or a version comparison from it), never on folder names.
Examples already in the code: the config pak only for builds after 6.00 CL-4395664 (`ConfigPak`), no
`Protocol=http` in DefaultEngine.ini from 7.00 (`PurchaseFlowConfig`), Creative servers from 7.00. Check a new key or
file against the newest build's code before writing it for every build: 7.10 exits at login on `Protocol=http`.
