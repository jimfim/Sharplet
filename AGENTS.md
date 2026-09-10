# AGENTS.md

Guidance for AI agents (and humans) working in this repository. Read this file before making changes; if it conflicts with a file in the repo, the repo wins.

## Project overview

Sharplet is a **reference implementation of a Kubernetes node agent (virtual kubelet) in C#**. It is not a production provider: this repo intentionally ships only **mock providers** that spoof running pods on the virtual node. Consumers of the published `Sharplet.Core` NuGet package implement their own `IPodController` / `INodeController` providers and wire up the kubernetes-side management for them.

Consequences for how you work here:

- Keep `Sharplet.Core`'s public API stable — it is a published package.
- The mock provider (`Sharplet.Provider.Mock`) doubles as **documentation** for provider implementers. Keep it simple and honest; it should demonstrate the interface contract, not real integration logic.
- Do **not** add real provider integrations to this repo.

## Repository layout

| Path | What it is |
| --- | --- |
| `src/Sharplet.slnx` | Solution (slnx format): Core (library), CSR (tools), Samplekubelet (implementation), plus Provider.Mock |
| `src/Sharplet.Core` | Class library, net10.0. Public contract: `IPodController`, `INodeController`, `IEventWatcher`. Hosted services (`PodControllerService`, `NodeControllerService`, `EventWatcherService`) watch the API server and report status. `SharpletExtensions.AddVirtualKubelet(SharpConfig)` is the DI entry point. Packaged to NuGet (`GeneratePackageOnBuild`). |
| `src/Sharplet.Provider.Mock` | Reference provider implementation (mock). The template 3rd parties should copy. |
| `src/Sharplet.Samplekubelet` | ASP.NET Core app, net10.0. The deployable kubelet: HTTPS on `10250` (client-cert), `10255` for metrics; entrypoint of the Docker image (`Sharplet.Samplekubelet.dll`). |
| `src/Sharplet.CSR` | Console helper for the kubelet certificate signing request flow. |
| `charts/sharplet` | Helm chart that deploys the Samplekubelet image. |
| `Dockerfile` | Multi-stage .NET 10 build → `aspnet:10.0` runtime. |
| `.github/workflows/main.yaml` | CI: `build` job (runs on push and PRs to `main`/`develop`) and `release` job (push only: GitVersion + NuGet push to GitHub Packages + Docker push + Helm chart-releaser). The `build` check should be required on protected branches so a failing build blocks merges. |
| `.github/workflows/sonarcloud.yaml` | SonarCloud analysis on every pull request. |
| `test.yaml` | Sample workload manifest for local cluster testing. |
| `.editorconfig` | **Source of truth for all formatting/style** — kept in line with the dotnet/sdk style rules. |

## Toolchain & common commands

- .NET 10 SDK (CI uses `10.x`).
- Package versions are managed centrally in `src/Directory.Packages.props` (Central Package Management) — `<PackageReference>` items in project files must **not** specify a `Version` attribute.
- Build: `dotnet build src` (CI runs `dotnet build src -c Release -p:version=<GitVersion semver>`).
- There are **no test projects yet**. If you add tests, create a new xUnit project under `src/` named `Sharplet.<Component>.Tests`, add it to `src/Sharplet.slnx`, and make sure `dotnet build src` still passes in CI.
- Docker image (from repo root): `docker build -t sharplet .`
- Deploy to a cluster: `helm install sharplet ./charts/sharplet`, then `kubectl apply -f test.yaml` to schedule a sample pod on the virtual node.

## Branching model

- `main` — release branch. Any push to it triggers a full publish (NuGet, Docker, Helm). **Never push here directly.**
- `develop` — integration branch for day-to-day work. **Never push here directly.**
- `feature/<short-kebab-description>` — one branch per change, e.g. `feature/mock-provider-in-core`. This is the established convention (see existing branches/PRs).
- `fix/<short-kebab-description>` — small, targeted fixes.
- `dependabot/*` — auto-created. Review and merge/close; never hand-edit.
- `gh-pages` — docs site only. Don't touch it from code branches.

Open a pull request for every change: feature/fix branches → `develop` (or `main` for hotfixes). Both CI and SonarCloud must be green before merging.

## Commits & pull requests

- Short lowercase imperative subject, one logical change per commit: `add pod status patching to controller service`, `fix event watcher null reference`.
- Optional conventional prefixes are fine (`feat:`, `fix:`, `chore:`), but the repo history is mostly plain lowercase subjects — match the tone of recent history.
- PR title: describe the change; PR body: why, what changed, and any cluster/runtime impact (config keys, ports, env vars).
- Don't commit `bin/`, `obj/`, local helm overrides (`values.local.yaml` is gitignored), or anything that looks like a secret/certificate.

## Code style

C# style follows the **dotnet/sdk** repository conventions (its root `.editorconfig` is the canonical rule set; keep this repo's `.editorconfig` in line with it). The existing code does not fully conform yet — write all new code to the rules below, and tidy files you touch. The `.editorconfig` at the repo root governs formatting (4-space indent, Allman braces, final newline, **UTF-8 with BOM** for `.cs`/`.csproj`); `ImplicitUsings` and `Nullable` are enabled in all projects.

File layout:

- File-scoped namespaces (`namespace Sharplet.Core;`).
- Usings above the namespace: `System.*` first, then the rest, sorted, nothing unused. No explicit `global using` lines — rely on `ImplicitUsings` for the standard ones.
- 4-space indent, Allman braces (newline before `{`; `else`/`catch`/`finally` on their own line), final newline, no trailing whitespace.
- Modifier order: `public` → `private` → `protected` → `internal` → `static` → `async`; always write accessibility modifiers explicitly.

Types and expressions:

- **No `var`**: always write the type name, including for built-ins (`int x = 0;`, not `var x = 0;`). Prefer predefined keywords (`int`, `string`, `bool`) over BCL names (`Int32`, `String`).
- Never qualify members with `this.`.
- Prefer target-typed `new()`, object/collection initializers, auto-properties, `??` / `?.` / `is null` over null checks, conditional expressions (`?:`) over if/else, and switch expressions over switch statements.
- Prefer pattern matching over `as`/cast checks, `throw` expressions and null-conditional delegate calls where they stay readable, static local functions, and explicit tuple element names.
- Expression-bodied members only for one-liners (properties, getters, single `return`/`throw`); block bodies otherwise.

Naming:

- `PascalCase` for types, members, and `const` fields (no prefix); `camelCase` for locals, parameters, and events; `I`-prefixed interfaces.
- `_camelCase` for private/internal instance fields; `s_` prefix for static fields.
- `async` members end in `Async` and take a `CancellationToken` (forward it to callees; no `ConfigureAwait` needed). Public API members default it to `default`, matching the existing interfaces.

Documentation:

- XML doc comments on the public surface of `Sharplet.Core` (types and members). Internal code: comment only when *why* isn't obvious.
- Recommended follow-up: adopt `Microsoft.CodeAnalysis.PublicApiAnalyzers` in `Sharplet.Core` with `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`, as dotnet/sdk does, so public API drift is caught at compile time.

Logging:

- Always `ILogger<T>` via constructor injection; structured templates with named holes — `_logger.LogInformation("pod {PodName} updated", name)`. Never string-interpolate into log messages, and never `Console.WriteLine` in library code.

## Architecture rules

- **Public API stability**: anything `public` in `Sharplet.Core` is shipped NuGet API. Don't remove or change signatures without an explicit breaking-change decision. Versioning is computed by GitVersion in CI (branch/tag driven) — **never hand-edit version numbers** in `.csproj` files.
- **Registration**: new services, watchers, controllers, and config are registered in `SharpletExtensions.AddKubelet(...)`. Use constructor injection everywhere; no statics, no service-locator patterns.
- **Provider boundary**: `Sharplet.Core` must stay provider-agnostic. Provider-specific logic lives in separate `Sharplet.Provider.*` projects that implement the Core interfaces — follow the `Sharplet.Provider.Mock` project as the reference layout.
- **Long-running work** lives in `BackgroundService` hosted services; all Kubernetes API calls go through the injected `IKubernetes` (from the `KubernetesClient` package, `k8s` / `k8s.Models` namespaces) and pass through the ambient `CancellationToken`.
- **Package upgrades**: `KubernetesClient` and other package versions (centralized in `src/Directory.Packages.props`) are managed by Dependabot. Don't hand-bump package versions; review the Dependabot PRs instead.

## CI, releases & the Helm chart

- A change is not done until `dotnet build src -c Release` passes locally **and** the CI + SonarCloud checks pass on the PR.
- CI (`.github/workflows/main.yaml`) computes the version with GitVersion and publishes: NuGet to GitHub Packages, Docker image as `jimjim/sharplet:<semver>`, and the Helm chart via chart-releaser.
- `charts/sharplet/Chart.yaml` keeps `version: 0.0.0` / `appVersion: 0.0.0` on purpose — CI rewrites them with `sed`. **Never commit a hand-bumped chart version**; only touch `Chart.yaml` when the chart structure itself changes.
- Local cluster overrides belong in `values.local.yaml` (gitignored) — never commit cluster-specific values.

## Definition of done (checklist)

1. `dotnet build src -c Release` passes with no new warnings.
2. Code style matches the Code style section and `.editorconfig` (no `var`, explicit type names, explicit accessibility modifiers, UTF-8 BOM, 4-space indent, final newline).
3. No public API of `Sharplet.Core` was changed without call-out; no package versions or chart versions hand-edited.
4. New services are DI-registered in `SharpletExtensions`; new public API has XML docs.
5. No secrets, local config, or build artifacts staged.
6. PR opened against `develop` (or `main` for hotfixes) with a clear title and description.