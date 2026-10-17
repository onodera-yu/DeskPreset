---
name: release
description: Cut a DeskPreset release - bump the version, verify the build, commit, tag vX.Y.Z and push so GitHub Actions publishes the zip. Use when the user asks to release, publish a new version, or bump the version.
---

# Release DeskPreset

Arguments: the new version (e.g. `1.1.0`) or `major` / `minor` / `patch`. If none is given, ask.

1. Check the tree: `git status` must be clean and the branch must be `main`. Otherwise stop and report.
2. Read the current `<Version>` in `Directory.Build.props` and compute the new one (semver).
3. Update `<Version>` in `Directory.Build.props`.
4. Verify: `dotnet build` must succeed, then `./build.ps1` must create `dist/DeskPreset-v<new>-win-x64.zip`.
   Launch `dist/publish-win-x64/DeskPreset.exe` with `DESKPRESET_HOME` set to a scratch folder, confirm the window
   opens, then close it.
5. Commit: `Release v<new>` (only `Directory.Build.props` should change).
6. Show the user the version, the commits since the previous tag (`git log <prev-tag>..HEAD --oneline`),
   and ask before pushing anything.
7. After approval: `git tag v<new>`, `git push origin main`, `git push origin v<new>`.
8. Tell the user the Actions run will attach the zip to the release at
   `https://github.com/<owner>/<repo>/releases/tag/v<new>` (owner/repo from `git remote get-url origin`).
