# VSNeo — Agent Guide

Visual Studio keeps its editor. Neovim is the brain behind it.

VSNeo is an in-process Visual Studio extension that embeds headless Neovim and synchronizes Vim modes, motions, operators, commands, cursor, folds, and viewport with the standard VS editor. VS keeps rendering, IntelliSense, refactorings, and undo.

Use this file for day-to-day work. Read `CLAUDE.md` for design rationale and detailed landmines; `README.md` is user-facing.

**Never add `Co-authored-by` or AI credit to commits.**

## Stack and layout

- Windows only; VS 2022 17.14+ and VS 2026 18.x; solution: `VSNeo.slnx`.
- SDK-style VSIX: `VSNeo_Extension/VSNeo_Extension.csproj`, .NET Framework 4.7.2, C# 14, nullable enabled.
- Neovim transport: hand-rolled msgpack-rpc over a Windows named pipe, never stdio or the MessagePack NuGet package.
- `VSNeo_Extension/Nvim/`: RPC client/session, state hub, msgpack, and span encoding.
- `VSNeo_Extension/Lua/vsneo.lua`: companion running inside nvim; pushes state and handles nvim-side synchronization.
- `VSNeo_Extension/Editor/`: key routing, buffer/cursor/viewport/fold synchronization, and WPF UI.
- `VSNeo_Extension/Infrastructure/`: circuit breaker, process job, column mapping, settings, and logging.
- `VSNeo.Seeky/`: embedded picker library; accesses the extension only through `ISeekyHost`.
- `SeekyEngine/`: out-of-process .NET 10 fff search engine, published into the VSIX.
- `external/Seeky/`: pinned submodule and the only source of shared Seeky files and `WebUI/index.html`. Never copy those files into other projects.
- `examples/`: sample `~/.vsneorc` and `~/.vsneorc.lua` configurations.
- `src/VSNeo/`: abandoned, gitignored pre-migration project.

## Non-negotiable architecture

> Key routing decides swallow-vs-passthrough from locally cached state with zero I/O. The key's effect travels asynchronously over RPC.

- `TextViewCreated` does bookkeeping only: no eager services, process starts, or RPC.
- Never use `JoinableTaskFactory.Run`, `.Result`, or `.Wait()` in startup or key paths. Use `RunAsync`, dispatcher scheduling, or `SwitchToMainThreadAsync`.
- Read `VSNeo_ExtensionPackage.Session` late; never capture it in a key-path constructor.
- Fallback is a session-level circuit breaker, never a per-keystroke retry.
- Insert-mode typing belongs to VS so IntelliSense, snippets, and brace completion work. Only `<Esc>`, `<C-w>`, `<C-o>`, opted-in named-key insert mappings, and named keys explicitly assigned by `:Sethandler ... i:vim` are claimed. Printable insert mappings cannot work.
- Ctrl+Alt(+Shift) and Shift+Alt chords pass through to VS (AltGr, VS commands, multi-caret, and box selection).
- Both `VsNeoKeyProcessor` (WPF) and `VsNeoCommandFilter` (`IOleCommandTarget`) are required. They act only on `Document`-role views and stand down for VS-owned foreign focus such as Roslyn's rename dashboard.
- One nvim buffer per file path, shared across VS text buffers. VS edits use spans; remote line events are applied in VS undo transactions.
- VS owns the visible document. Follow nvim-initiated switches only to real files; snap back from scratch, directory, or missing targets. Drop mismatched cursor/scroll reports while buffers disagree.
- Fold boundaries come from VS outlining; open/closed state synchronizes both ways. `zf` creates a real VS region through `UserFoldTagger`.
- `ViewportSynchronizer` matches nvim's grid and topline to VS. Caret visibility and vertical `scrolloff` padding come from laid-out lines, not line arithmetic; CodeLens and folds make those differ. External toplines retain nvim's native padding correction.

## Configuration and Seeky

- The companion sources `~/.vsneorc`, then `~/.vsneorc.lua`, then reasserts sync-critical options. Use Lua mappings when `desc`/which-key labels matter.
- `:Vsc Some.Command` runs a VS command; `:Vsclist` opens Seeky's `vscommands` picker and `:Sethandler` selects key ownership. `:VSNeoToggle` disables input without stopping the mirrors. Default `<C-o>`/`<C-i>` use VS navigation history.
- Opt-in plugins live under `~/.vsneo/pack/<group>/{start,opt}/`. Only buffer/motion-layer plugins fit; UI and window-management plugins do not.
- Seeky is embedded, not a second extension. `vsneo.seeky` notifies the package; `SeekyPickerController` hosts Seeky's WebUI over WebView2.
- The picker never references the extension directly. Logging, process lifetime, and nvim access cross `ISeekyHost` (`Seeky/VsNeoSeekyHost.cs`).
- Change shared Seeky files upstream, then update the submodule. See `docs/seeky.md` and `docs/seeky-development.md`.

## Build, run, and debug

Requires the Visual Studio extension development workload.

```cmd
msbuild VSNeo.slnx -restore -p:Configuration=Debug
```

Output: `VSNeo_Extension\bin\Debug\net472\VSNeo_Extension.vsix`.

`dotnet build` is not sufficient for the VSIX. Deploy locally with **F5**, not command-line MSBuild or VSIXInstaller; otherwise the experimental instance can load an older build.

If nvim is not on `PATH`:

```cmd
set VSNEO_NVIM_PATH=C:\path\to\nvim.exe
```

Debug key tracing (Debug builds only):

```cmd
set VSNEO_TRACE_KEYS=1
```

Logs: `%TEMP%\vsneo.log`. If F5 cannot resolve the startup program:

```cmd
msbuild VSNeo_Extension\VSNeo_Extension.csproj -getProperty:StartProgram
```

CI (`.github/workflows/build.yml`) gates VSIX packaging and publishing on tests. Master publishes to Open VSIX; `v*` tags publish a GitHub release and Marketplace update. CI stamps versions and suppresses local `BumpVersion.ps1`. See `docs/publish-to-marketplace.md`.

## Code style

- Prefer explicit null checks over null-forgiving operators.
- Comments explain why, not what the next line does.
- Keep the key path nonblocking, allocation-conscious, and free of I/O and logging.
- Document UI-thread-only fields and assert with `ThreadHelper.ThrowIfNotOnUIThread()` at entry points.
- `VSTHRD001` suppressions for deliberate dispatcher priorities need the measured reason.
- All UTF-8 byte-column ↔ UTF-16 character-column conversion goes through `ColumnMapper`.

## Tests

Run all companion suites against real headless Neovim:

```cmd
pwsh tests\run-tests.ps1
```

Run the SDK-independent C# tests:

```cmd
dotnet test tests\dotnet\VSNeo.Tests
```

Benchmark hot paths when changing them:

```cmd
dotnet run -c Release --project tests\dotnet\VSNeo.Benchmarks -- --filter '*' --job short --memory
```

The Lua runner isolates HOME/USERPROFILE and finds nvim via `-NvimPath`, `VSNEO_NVIM_PATH`, or `PATH`. Copy `tests/helper.lua` patterns for new `tests/*_tests.lua` suites. Extend Lua tests for companion changes.

`NvimWireTests` exercises the real RPC attach/prime/input path. Key routing, caret behavior, WPF UI, and viewport integration still require real VS + nvim: deploy with F5, exercise the affected behavior, and inspect `%TEMP%\vsneo.log`. For C# changes, add focused manual scenarios unless that stack is available.

## Packaging and security

- `source.extension.vsixmanifest` must register both package and MEF assets; missing MEF registration silently leaves keys unintercepted.
- `Lua/vsneo.lua` ships as content beside the DLL for both F5 and packaged use.
- The extension runs in `devenv.exe` with user privileges. User mappings and the companion can intentionally execute arbitrary VS commands.
- nvim comes from `PATH` or `VSNEO_NVIM_PATH`; it must be trusted.
- A Windows job with `KILL_ON_JOB_CLOSE` prevents orphaned nvim processes.

## Known landmines

Read `CLAUDE.md` before changing these:

1. **No stdio transport.** Windows anonymous-pipe semantics silently break nvim; keep the named-pipe client.
2. **No MessagePack package.** VS owns its assembly; a second copy breaks MEF composition.
3. **Never construct `BufferMirror` directly.** Use `BufferMirror.ForDocument`.
4. **Handle `nvim_buf_detach_event`.** Buffer unload/reload otherwise leaves mirrors deaf.
5. **Null `changedtick` is not an edit.** It is an `'inccommand'` preview.
6. **Use `ColumnMapper` everywhere columns cross the boundary.**
7. **Ship neither WebView2 nor System.Text.Json.** VS supplies them, including WebView2's native loader; duplicate assemblies break the in-process host.

## Starting points

- Startup: `VSNeo_ExtensionPackage.cs` → `Nvim/NvimSession.cs` → `Nvim/NvimRpcClient.cs`.
- Keys: `Editor/VsNeoKeyProcessorProvider.cs` and `Editor/VsNeoCommandFilter.cs`.
- Buffers: `Editor/BufferMirror.cs`.
- Cursor/selection: `Editor/CursorSynchronizer.cs`.
- Viewport/folds: `Editor/ViewportSynchronizer.cs` and `Editor/FoldSynchronizer.cs`.
- Companion: `Lua/vsneo.lua`.
- Debugging: `%TEMP%\vsneo.log` with `VSNEO_TRACE_KEYS=1`.
