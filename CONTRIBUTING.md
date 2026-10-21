# Contributing to Sharplet

Thank you for your interest in Sharplet! All contributions — code, documentation, or bug reports — are welcome.

## Getting Started

Build the project:

```bash
dotnet build src -c Release
```

Package versions are managed centrally in `src/Directory.Packages.props` (Central Package Management). `Version` attributes on `<PackageReference>` items must **not** be specified in project files.

## Branching Model

- **`main`** — release branch. Any push triggers a full publish (NuGet, Docker, Helm). Never push here directly.
- **`develop`** — integration branch for day-to-day work. Pushes to develop publish prerelease versions. Never push here directly.
- **`feature/<short-description>`** — new features. One branch per change.
- **`fix/<short-description>`** — targeted bug fixes.

Open a pull request from your feature/fix branch targeting `develop`. The CI build check must pass before merging.

## Code Style

Sharplet follows the [dotnet/sdk](https://github.com/dotnet/sdk) repository conventions. The `.editorconfig` at the repo root is the source of truth; keep it in line with dotnet/sdk rules. Key requirements:

| Rule | Detail |
| --- | --- |
| No `var` | Always write explicit type names, including built-ins (`int x = 0`, not `var x = 0`) |
| Braces | Allman style — opening brace on its own line |
| Access modifiers | Always explicit (`public`, `private`, etc.) |
| Namespace | File-scoped namespace declarations |
| Encoding | UTF-8 with BOM |
| Indent | 4 spaces |
| Naming | `PascalCase` types/members, `camelCase` locals/events, `I`-prefixed interfaces |

Other conventions: prefer switch expressions over statements, target-typed `new()`, pattern matching over `as`/cast, expression-bodied one-liners, structured logging via `ILogger<T>` with named holes (never string interpolation into log messages). See [AGENTS.md](./AGENTS.md) for the complete rule set.

## Definition of Done

Before submitting a PR, ensure all of the following hold:

1. `dotnet build src -c Release` passes locally with no new warnings.
2. Code style matches the rules above (no `var`, explicit types, explicit accessibility modifiers, UTF-8 BOM, 4-space indent).
3. Public API changes in `Sharplet.Core` are intentional and documented with XML comments.
4. New services are registered via DI in `SharpletExtensions.AddVirtualKubelet(...)`.
5. No secrets, build artifacts, or local config committed.
6. Pull request targets `develop` with a clear title and description.

## License

By contributing, you agree that your contributions will be licensed under the project's [MIT License](./LICENSE).
