# Contributing to Sharplet

Thank you for your interest in Sharplet! This document covers the workflow and conventions we follow.

## Branch model

- **`main`** — release branch. Triggers full publishes (NuGet, Docker, Helm). Never push here directly.
- **`develop`** — integration branch. Pushes publish prerelease versions. Never push here directly.
- **`feature/<short-description>`** — one branch per change (e.g. `feature/add-certificate-support`).
- **`fix/<short-description>`** — targeted fixes.

Open a pull request for every change, targeting `develop` (or `main` for hotfixes). The CI build check must pass before merging.

## How to build

```bash
dotnet build src -c Release
```

All builds use .NET 10 SDK. Use `dotnet run --project src/Sharplet.Samplekubelet` to run the sample kubelet locally.

## Code style

C# follows the [dotnet/sdk repository conventions](https://github.com/dotnet/sdk/blob/main/.editorconfig). The `.editorconfig` at the repo root is the source of truth; keep it in line with those rules. All new code must conform — tidy files you touch even outside the immediate change.

Key rules:

- **No `var`** — always write explicit type names (`int x = 0;`, not `var x = 0;`). Prefer keywords (`int`, `string`) over BCL names (`Int32`, `String`).
- **Allman braces** — opening brace on the next line:

  ```csharp
  if (condition)
  {
      DoSomething();
  }
  ```

- **Explicit types and accessibility modifiers** — always write them out. Never omit `public`, `private`, etc.
- **4-space indentation** — defined in `.editorconfig` (`indent_size = 4`).
- **UTF-8 with BOM** — required for `.cs` and `.csproj` files.
- **Final newline** at end of every file. No trailing whitespace.

### File layout

- File-scoped namespaces (`namespace Sharplet.Core;`).
- Usings above the namespace: `System.*` first, then others, sorted, no unused imports. No `global using` lines.
- Modifier order: `public` → `private` → `protected` → `internal` → `static` → `async`.

### Naming

- `PascalCase` for types, members, const fields. `camelCase` for locals, parameters, events. `I`-prefixed interfaces.
- `_camelCase` for private/internal instance fields. `s_` prefix for static fields.
- `async` methods end in `Async` and accept a `CancellationToken`.

### Idioms

- Target-typed `new()`, object/collection initializers, `??` / `?.`, switch expressions, pattern matching.
- Expression-bodied members only for one-liners (properties, getters, single return/throw).
- Always `ILogger<T>` via constructor injection; structured templates with named holes. Never string-interpolate into log messages.

## Central Package Management (CPM)

Package versions are centralized in `src/Directory.Packages.props`. When adding or updating packages:

1. Set the version in `Directory.Packages.props` — add a `<PackageVersion Include="Name" Version="x.y.z" />` entry.
2. In project files, reference the package **without** a `Version` attribute:

   ```xml
   <PackageReference Include="SomePackage" />
   ```

3. Run `dotnet restore` to regenerate `packages.lock.json`. Dependabot manages most upgrades — review and merge Dependabot PRs rather than hand-bumping versions.

## Definition of done

Before requesting a review, make sure:

1. `dotnet build src -c Release` passes with no new warnings.
2. Code style matches the conventions above (no `var`, explicit types/modifiers, UTF-8 BOM, 4-space indent, final newline).
3. No public API of `Sharplet.Core` was changed without being called out in the PR description.
4. New services are registered in `SharpletExtensions`; new public API surface has XML doc comments.
5. No secrets, local config overrides, or build artifacts are staged.
6. The PR targets `develop` (or `main` for hotfixes) with a clear title and description covering the why, what changed, and any runtime impact.
