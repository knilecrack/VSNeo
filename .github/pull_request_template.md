## What and why

<!-- One paragraph. Link the issue if there is one. -->

## How to test

<!-- The keys or steps a reviewer should try, and what to look for. Key-routing
     changes: name the mode and the expected swallow-vs-passthrough. -->

## Checklist

- [ ] `pwsh tests/run-tests.ps1` passes (companion changes)
- [ ] `dotnet test tests/dotnet/VSNeo.Tests` passes
- [ ] The three `dotnet format whitespace` checks in AGENTS.md pass, run from the repo root
- [ ] New options, commands and mappings are in `docs/options.md`
- [ ] No `JoinableTaskFactory.Run`, `.Result` or `.Wait()` on a key or startup path
- [ ] No MessagePack package, stdio transport, or extra shipped assemblies
- [ ] F5-tested in the experimental instance (key, caret, viewport or UI changes)
