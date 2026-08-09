# Release process

Releases use semantic versions and ship as a portable, self-contained Windows x64 ZIP.

## Prepare

1. Work from a clean, current `main` branch.
2. Update `Version`, `AssemblyVersion`, `FileVersion`, and `InformationalVersion` in `Directory.Build.props`.
3. Move the release notes into a dated section in `CHANGELOG.md`.
4. Run the full test suite.

```powershell
git fetch origin
git switch main
git pull --ff-only
dotnet test ModelExplorer.slnx --configuration Release
```

## Package

```powershell
./scripts/Publish-Release.ps1 -Version 1.0.0
```

This produces `artifacts/3DModelExplorer-1.0.0-win-x64.zip`. Extract the archive into a temporary folder and launch `ModelExplorer.exe` before publishing it.

## Publish

1. Commit and push the release preparation.
2. Create an annotated `v1.0.0` tag at that commit and push it.
3. Create a GitHub Release from the tag.
4. Use the matching CHANGELOG section as release notes.
5. Attach the generated ZIP as a binary asset.
6. Verify the asset can be downloaded and its SHA-256 hash matches the local artifact.

Do not include local model files, the SQLite index, thumbnail caches, PDB files, or signing credentials in an artifact.
