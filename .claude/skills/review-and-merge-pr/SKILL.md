---
name: review-and-merge-pr
description: Use for every open Core-Launcher pull request - check it out in a worktree, review it against the launcher's rules, rework what is wrong (on the PR branch or on main right after), build and test, merge it, comment on what was changed, and tag a release. Reject only what the project has ruled out.
---

# Review, fix and merge a Core-Launcher pull request

The owner's rule: agents review every open PR, make it right and merge it themselves. Rules: AGENTS.md sections 1,
2 and 6. Release steps: `docs/agents/releasing.md`.

1. Read `H:\CoreWork\HANDOFF.md` (skip PRs someone is already on). `gh pr list -R PongooDev/Core-Launcher --state open`.
2. Read it: `gh pr view <n> -R PongooDev/Core-Launcher --comments`, `gh pr diff <n> -R PongooDev/Core-Launcher`.
3. Reject only if it adds something ruled out (CoreClient changes without the owner's OK, writing into game builds
   beyond what the launcher already writes, a risky feature on by default):
   `gh pr close <n> -R PongooDev/Core-Launcher --comment "<why>"`. Stop.
4. Worktree and checkout:
   ```
   git -C $env:USERPROFILE\Documents\GitHub\Core-Launcher fetch origin
   git -C $env:USERPROFILE\Documents\GitHub\Core-Launcher worktree add --detach H:\CoreWork\lapr-<n> origin/main
   cd H:\CoreWork\lapr-<n>
   gh pr checkout <n> -R PongooDev/Core-Launcher
   ```
5. Review: downloads size and SHA-256 checked; build-specific behaviour gated by `KnownBuilds`; no warnings
   suppressed; tests added; README updated for user-facing changes; no keys, tokens or secrets.
6. Rework what is wrong. If `gh pr view <n> -R PongooDev/Core-Launcher --json maintainerCanModify` is true, commit on
   the PR branch and `git push`. If not, merge first (step 8) and fix on main right after, in this session. Commit
   trailers as AGENTS.md section 7 (Freebuff/Codebuff: `Agent: Freebuff`); the pre-commit hook runs, never
   `--no-verify`.
7. `dotnet build`, then `launcher_tests` (core MCP; poll its `job_id` until done) or
   `tests\Core.Launcher.Tests\bin\Debug\net10.0-windows\win-x64\Core.Launcher.Tests.exe`.
8. Merge with the repo's usual merge commit: `gh pr merge <n> -R PongooDev/Core-Launcher --merge`.
9. Comment on the PR: what you changed and why, and what was verified.
10. Fixes still due on main: make them now, then tag the next release on `origin/main` and check its run
    (`docs/agents/releasing.md`). Comment the release link on the PR.
11. Remove the worktree. Write anything unfinished in `HANDOFF.md`.

Done when: the PR is merged (or rejected with a reason), every rework is on main, tests pass, and a release is tagged
with a green run.
