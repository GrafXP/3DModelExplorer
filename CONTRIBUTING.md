# Contributing to 3D Model Explorer

Thank you for helping improve the project.

## Before starting

- Search existing issues and pull requests before opening a duplicate.
- Use an issue to discuss large features or architectural changes first.
- Keep changes focused; unrelated cleanup makes review and rollback harder.
- Follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Development setup

Development currently requires Windows and the .NET 10 SDK.

```powershell
git clone https://github.com/GrafXP/3DModelExplorer.git
cd 3DModelExplorer
dotnet restore ModelExplorer.slnx
dotnet build ModelExplorer.slnx
dotnet test ModelExplorer.slnx
```

Run the application with:

```powershell
dotnet run --project src/ModelExplorer.App
```

## Pull requests

1. Branch from the current `main` branch.
2. Add or update tests for behavior changes.
3. Run `dotnet test ModelExplorer.slnx` before submitting.
4. Update README or CHANGELOG content when user-facing behavior changes.
5. Explain the problem, the chosen approach, manual verification, and known tradeoffs in the pull request.

The solution uses nullable reference types, implicit usings, and the latest C# language version. Match the surrounding style, keep UI work responsive, and do not perform model parsing or filesystem scans on the WPF UI thread.

## AI-assisted contributions

AI-assisted contributions are welcome, but the contributor remains responsible for every submitted line. In the pull request:

- disclose meaningful use of generative AI;
- review generated code and documentation for correctness and licensing issues;
- remove secrets, private data, fabricated citations, and irrelevant generated content;
- include tests appropriate to the risk of the change.

See [AI_ASSISTED_DEVELOPMENT.md](AI_ASSISTED_DEVELOPMENT.md) for the project's own disclosure.

## Licensing

By submitting a contribution, you agree that it may be distributed under the project's [MIT License](LICENSE). Only submit work you have the right to license.
