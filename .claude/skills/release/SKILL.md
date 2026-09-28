---
name: release
description: Use when cutting a godman release — bumping the version, tagging, and publishing to GitHub Releases and WinGet
disable-model-invocation: true
argument-hint: <X.Y.Z>
---

# Release godman

Pushing a `v*` tag is the publish step, and it cannot be taken back: `release.yml`
builds the win-x64/linux-x64 archives, creates the GitHub release (which `install.sh`
immediately serves as "latest"), and opens a WinGet PR. Confirm with the user before
pushing the tag.

## Gotchas

- **The artifact version comes from the tag, not the csproj.** `release.yml` strips the
  `v` from the tag name. Forgetting the csproj bump ships binaries whose `--version`
  disagrees with the release — nothing in CI catches it.
- **A hyphenated tag skips WinGet** (`v1.4.1-beta1`). Use that for pre-releases.
- **Tag `main`, after the PR merges.** Every past tag (`v1.1.0`…`v1.3.0`) sits on main.

## Steps

1. On the release branch, set both in `GodotManager/GodotManager.csproj`:
   `<Version>X.Y.Z</Version>` and `<AssemblyVersion>X.Y.Z.0</AssemblyVersion>`.
2. Mark the phase complete in `GodotManager/docs/PLAN.md`; update README.md if user-facing
   behaviour changed.
3. `dotnet test -v minimal` must pass. Commit `chore(release): bump to X.Y.Z`.
4. Merge the PR to `main` (`gh pr merge`), then `git checkout main && git pull`.
5. **Ask the user**, then: `git tag vX.Y.Z && git push origin vX.Y.Z`.
6. `gh run watch` on the `release` run; confirm `gh release view vX.Y.Z` lists
   `godman-windows-x64-X.Y.Z.zip`, `godman-linux-x64-X.Y.Z.tar.gz`, and checksums.
