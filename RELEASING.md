# Releasing United Sets X

A release is a portable zip (`UnitedSetsX-<version>-win-x64.zip`) attached to a GitHub release.
It contains .NET and the Windows App SDK, so users only extract it and run `UnitedSets.exe`.

## 1. Pick the version

Use [semantic versioning](https://semver.org): `1.0.0`, `1.0.1` for fixes, `1.1.0` for new features.
Optionally update `<Version>` in `UnitedSets/UnitedSets.csproj` (it is the default for local builds;
release builds always use the version passed to the script).

## 2. Check the build locally (optional but recommended)

```powershell
./scripts/publish.ps1 -Version 1.0.0
```

This creates `artifacts/UnitedSetsX-1.0.0-win-x64.zip`. Extract it to another folder, run `UnitedSets.exe`
and try grouping a few windows.

## 3a. Automatic release (GitHub Actions)

Push a tag that starts with `v`:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The *Release* workflow (`.github/workflows/release.yml`) builds the zip and creates a **draft** release.
Open the repository's *Releases* page, edit the notes if needed and click **Publish release**.

## 3b. Manual release (without GitHub Actions)

1. Build the zip locally (step 2).
2. On GitHub: *Releases* › *Draft a new release*.
3. *Choose a tag*: type `v1.0.0` and select *Create new tag on publish* (target: `master`).
4. Title `United Sets X 1.0.0`, write the notes (or click *Generate release notes*).
5. Drag `artifacts/UnitedSetsX-1.0.0-win-x64.zip` into the assets area, then **Publish release**.

## Notes

- The zip is not code signed: Windows SmartScreen may warn the first time it is run ("More info" › "Run anyway").
- An ARM64 build can be made with `./scripts/publish.ps1 -Version 1.0.0 -Platform arm64`.
