## Why

(what problem this solves, or which issue it closes)

## What changed

-

## Cluster / runtime impact

(config keys, ports, env vars, image or chart changes — write "none" if not applicable)

## Definition of done

- [ ] `dotnet build src -c Release` passes with no new warnings
- [ ] Code style matches the AGENTS.md Code style section and `.editorconfig` (no `var`, explicit type names, explicit accessibility modifiers, UTF-8 BOM, final newline)
- [ ] No `Sharplet.Core` public API changed without call-out; no package versions or chart versions hand-edited
- [ ] New services are DI-registered in `SharpletExtensions`; new public API has XML docs
- [ ] No secrets, local config, or build artifacts staged