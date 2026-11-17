# Releasing Core-Launcher

Players and the owner get launcher changes only through the launcher's auto-update, which reads this repo's latest
GitHub release. So **every code change is followed by a release tag**, right after it is pushed, without waiting for
anyone to test it first.

## What needs a release

- Any change under `src/` or to the projects' build settings: yes.
- Test-only changes: no, unless they come with a code change.
- Docs only (`README.md`, `AGENTS.md`, `docs/`, `.claude/`, `.github/ISSUE_TEMPLATE/`): no.

## Steps

1. The change is pushed to `origin/main`, and `dotnet build` plus the tests passed locally.
2. Find the latest tag and pick the next one: patch (`v1.0.69` to `v1.0.70`) for fixes and UI tweaks, minor
   (`v1.1.0`) for features.
   ```
   git fetch origin --tags
   git tag --sort=-v:refname | Select-Object -First 3
   ```
3. Tag the pushed commit and push the tag:
   ```
   git tag v1.0.70 origin/main
   git push origin v1.0.70
   ```
   Another agent may have tagged in the meantime: if the push is refused, fetch tags and take the next number.
4. Watch the Release workflow (`release.yml`: tests, publishes `CoreLauncher.exe` and `CoreServerHost.exe`, creates
   the release with generated notes):
   ```
   gh run list -R PongooDev/Core-Launcher --workflow release.yml --limit 1
   gh run watch <run id> -R PongooDev/Core-Launcher
   gh release view v1.0.70 -R PongooDev/Core-Launcher
   ```
5. If the run fails, fix forward with a new commit and a new tag. Never move or delete a published tag.
6. Put the release link in the issue comment and the report.

A tag with a suffix (`v1.1.0-beta`) becomes a pre-release, which the updater skips.
