---
name: launcher-change-and-release
description: Use for any Core-Launcher code change (a fix, a feature, a new or promoted build in KnownBuilds.cs) - worktree, change, tests with the test exe, self-review, commit, push, then tag and push the next release and check its run. Docs-only changes skip the release.
---

# Change Core-Launcher and release it

Rules: AGENTS.md sections 1 and 2. Depth: `docs/agents/knownbuilds.md`, `docs/agents/releasing.md`.

1. Read `H:\CoreWork\HANDOFF.md`; run the core MCP's `project_status` (if available).
2. Read the issue and comments: `gh issue view <n> -R PongooDev/Core-Launcher --comments`.
3. Worktree:
   ```
   git -C C:\Users\tkenn\Documents\GitHub\Core-Launcher fetch origin
   git -C C:\Users\tkenn\Documents\GitHub\Core-Launcher worktree add -b fix-<n> H:\CoreWork\la-<n> origin/main
   ```
4. Make the change. Build-specific behaviour is gated by CL through `KnownBuilds`. Nothing new written into game
   builds; downloads stay size and SHA-256 checked; unproven features off by default.
5. Add or update tests in `tests/Core.Launcher.Tests/`.
6. Test: `launcher_tests` (core MCP, if available), else
   ```
   dotnet build
   tests\Core.Launcher.Tests\bin\Debug\net10.0-windows\win-x64\Core.Launcher.Tests.exe
   ```
   Never run `CoreLauncher.exe` or `CoreServerHost.exe` on the owner's PC.
7. Self-review the diff: only your files staged; no warnings suppressed; no keys or tokens; README updated for
   user-facing changes; the message says what was verified by tests and what a real install still has to show.
8. Commit. Freebuff/Codebuff commits end with `Agent: Freebuff` (no Claude Co-Authored-By line); Claude uses its own.
9. `git fetch origin`, `git rebase origin/main`, rebuild and retest if needed, `git push origin HEAD:main`.
10. Release (code changes only):
    ```
    git fetch origin --tags
    git tag --sort=-v:refname | Select-Object -First 1
    git tag v<next> origin/main
    git push origin v<next>
    gh run list -R PongooDev/Core-Launcher --workflow release.yml --limit 1
    ```
    Watch the run until it passes (`gh run watch <id> -R PongooDev/Core-Launcher`).
11. Comment on the issue with the commit and the release link; close it if fixed.
12. Remove the worktree and branch. Write anything unfinished in `H:\CoreWork\HANDOFF.md`.

Done when: tests pass, the change is on `origin/main`, the release run is green (or the change is docs-only), and
the issue names the commit and release.
