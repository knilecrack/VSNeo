# VSNeo — Agent Guide

Visual Studio keeps its editor. Neovim is the brain behind it.

VSNeo is an in-process Visual Studio extension that embeds a headless Neovim instance and forwards Vim semantics (modes, motions, operators, the command line) into the standard Visual Studio text editor. Visual Studio keeps rendering, IntelliSense, refactorings, and undo. The two are kept in sync through a mirrored buffer.

This file is a working reference for AI coding agents. Read `CLAUDE.md` for the full design rationale and known landmines (`README.md` is the user-facing storefront).

## Technology stack

- **Platform**: Windows only.
- **Project type**: Visual Studio SDK-style VSIX extension (`VSNeo_Extension/VSNeo_Extension.csproj`).
- **Target framework**: .NET Framework 4.7.2 (`net472`). Fixed by Visual Studio's in-process extension model.
- **Language**: C# 14 with nullable reference types enabled.
- **Visual Studio targets**: VS 2022 17.14+ and VS 2026 18.x (the solution is in the new `.slnx` format).
- **Neovim transport**: msgpack-rpc over a Windows named pipe (`\\.\pipe\vsneo-<guid>`). Not stdio. Not the MessagePack NuGet package.
- **Editor integration**: WPF `KeyProcessor`, `IOleCommandTarget` command filter, MEF-exported text-view creation listeners, WPF margin provider.
- **Companion script**: `Lua/vsneo.lua` runs inside nvim and pushes mode/cursor/viewport state back over RPC.

## Project layout

```
VSNeo.slnx                              New-format solution
VSNeo_Extension/
  VSNeo_Extension.csproj                Main (only) project
  source.extension.vsixmanifest         VSIX metadata and MEF asset registration
  VSNeo_ExtensionPackage.cs             AsyncPackage: background load, owns nvim lifetime
  Lua/vsneo.lua                         Companion script shipped beside the DLL
  Nvim/
    MsgPack.cs                          Hand-rolled msgpack reader/writer/stream framer
    NvimRpcClient.cs                    msgpack-rpc over named pipe
    NvimSession.cs                      attach/activate split, nvim_input, ui_attach
    NvimStateHub.cs                     redraw/state notifications -> cached mode + cursor + cmdline + wildmenu
    NvimLua.cs                          Loads Lua/vsneo.lua from beside the assembly
  Editor/
    VsNeoKeyProcessorProvider.cs        Synchronous WPF key interception
    VsNeoCommandFilter.cs               IOleCommandTarget filter (Escape, Paste, CmdLine keys)
    IntelliSenseGate.cs                 Is a VS completion/signature list open?
    KeyEncoder.cs                       WPF keys -> nvim notation; Ctrl+Alt chords pass through
    BufferMirror.cs                     VS <-> nvim two-way buffer sync
    CursorSynchronizer.cs               Caret and selection in both directions
    ViewportSynchronizer.cs             Grid size and topline for <C-d>/H/M/L/zz; one-line edge scrolls become half-screen jumps
    TextViewCreationListener.cs         Focus-based mirror attachment (bookkeeping only)
    CmdLineOverlayWindow.cs             Session-level cmdline + wildmenu as a shell-owned, non-activatable window (Ctrl+Q shape)
    CmdLinePopup.cs                     Per-view floating cmdline popup, currently disabled; superseded by CmdLineOverlayWindow
    SearchHighlightAdornment.cs         hlsearch matches, current match in CurSearch color
    YankFlashAdornment.cs               Briefly highlights yanked text (TextYankPost)
    OverlayLabelsAdornment.cs           Renders labels pushed by Lua overlay interactions (jump letters)
    RegistersPopup.cs                   Register peek on " in normal/visual; dismisses when showcmd clears
    SplitNavigator.cs                   Directional <C-w>h/j/k/l between tab groups via frame geometry; MRU walk (<C-6>)
    TabJumper.cs                        Labeled jump to any open tab (gb) via VsFramePropID.OverrideCaption
  Infrastructure/
    CircuitBreaker.cs                   Session-level fallback on repeated faults
    ProcessJob.cs                       KILL_ON_JOB_CLOSE so nvim cannot orphan
    ColumnMapper.cs                     UTF-8 byte <-> UTF-16 char conversions
    KeyBindingCleaner.cs                Removes VS chords Vim needs
    Log.cs                              Lifecycle diagnostics -> %TEMP%\vsneo.log
examples/vsneorc.vim                    Sample user config (copy to ~/.vsneorc); ported VsVim mappings
src/VSNeo/                              Abandoned pre-migration project; gitignored and superseded
```

## Architecture at a glance

The design is built around one invariant:

> The key handler decides swallow-vs-passthrough from a locally cached mode, with zero I/O. The effect of the key travels over RPC and lands a few milliseconds later.

Corollaries that are non-negotiable in this codebase:

- `TextViewCreated` does bookkeeping only. No eager work there.
- No `JoinableTaskFactory.Run`, no `.Result`, no `.Wait()` in the startup path or key path. `RunAsync` only.
- Fallback is a session-level circuit breaker, never a per-keystroke retry.
- Insert mode passes through to Visual Studio untouched so IntelliSense, snippets, and brace completion keep working. Only `<Esc>` is claimed in insert.
- Ctrl+Alt(+Shift) chords are never claimed. Ctrl+Alt is AltGr on many layouts (indistinguishable to nvim) and is Visual Studio's own binding namespace, so `KeyEncoder` passes those chords straight through.

User configuration is opt-in: after the companion sets everything up, `Lua/vsneo.lua` sources `~/.vsneorc` (vimscript) if it exists, then re-asserts the sync-critical options (`wrap`, `scrolloff`, `laststatus`, `swapfile`) the viewport and buffer mirror rely on. A `:Vsc Some.Command` command (plus a position-guarded `:vsc` cmdline abbreviation) runs any Visual Studio command by name, so VsVim-style `.vsvimrc` mappings port nearly verbatim — see `examples/vsneorc.vim`. Insert-mode mappings (`inoremap`) cannot work here; insert keys never reach nvim — the only insert-mode keys claimed are `<Esc>` and `<C-w>`. `<C-w>` (delete word backward) is performed Visual Studio-side: nvim's insert-mode cursor cannot be pushed onto the caret reliably (an API-set cursor at end-of-line is clamped when the next key is processed, deleting one character short), so `vsneo.word_back_boundary(row, col)` only computes the byte column `i_CTRL-W` would stop at and `VsNeoKeyProcessor.DeleteWordBackward` deletes the span in VS, where the mirror carries it to nvim like typed text. The chord is claimed even while a completion list is open.

Plugins are opt-in through the standard packages layout rooted at `~/.vsneo`: `pack/<group>/start/<plugin>` loads at startup, `pack/<group>/opt/<plugin>` is `:packadd`-able from the rc. The root is appended to `packpath` via `--cmd` in `NvimRpcClient` because it must happen before startup's `packloadall` — after startup, both `packloadall` and `:packadd` silently ignore `start` directories (verified on nvim 0.12). The user's regular nvim plugins are deliberately not loaded. Only plugins that live in nvim's buffer/motion layer can work here (surround, commentary, text objects); UI plugins render to nvim's grid, which nothing displays, and window-management plugins fight the single-window viewport model.

The VS-side highlight adornments (search matches, current match, yank flash) take their colors from nvim's own `Search`, `CurSearch`, and `IncSearch` groups: the companion pushes them as `vsneo_highlights` after the rc loads and on `ColorScheme`, so `:hi Search guibg=…` in `~/.vsneorc` works. The yank flash rides `TextYankPost` as `vsneo_yank` (operator-filtered to `y`).

Overlay interactions are how Lua gets pixels: `vsneo_overlay_active` opens one (the command filter then routes Escape/Enter/Backspace/arrows to nvim, exactly as in CmdLine mode) and `vsneo_overlay_labels` carries `[line, startByte, endByte, text]` entries — empty text marks a background span, text draws a label box — which `OverlayLabelsAdornment` renders. `vsneo.jump()` (mapped to `s` in normal mode; native `s` is `cl`'s synonym) is the flash.nvim-style jump built on it, and `f`/`F`/`t`/`T` get the same labels when the line holds several matches (the landing is always the native motion fed with a count, so `;` and `,` keep working; a non-label key falls back to the plain first-match motion). flash.nvim itself cannot work here: its labels are extmark virtual text on nvim's grid, and nvim 0.12 reports no extmarks to external UIs.

There are two key interception points by necessity:

1. `VsNeoKeyProcessor` sees WPF `PreviewKeyDown` and `TextInput` for characters and chords.
2. `VsNeoCommandFilter` sits on the `IOleCommandTarget` chain because Visual Studio turns keys like `Escape`, `Ctrl+[`, `Enter`, arrows, etc., into commands before WPF ever sees them.

Both attach to any `Editable` view but must act only on `Document`-role views (`_view.Roles.Contains(PredefinedTextViewRoles.Document)`) — the same condition mirrors attach under. Tool windows like the C# Interactive window are editable text views too, and they own their keystrokes outright; without the check, Normal mode swallows everything typed into the REPL.

The buffer mirror keeps one nvim buffer per file path, shared across `ITextBuffer` instances. Edits are sent as spans (`nvim_buf_set_text`) and applied back from `nvim_buf_lines_event` notifications, grouped into `ITextUndoHistory` transactions.

## Build commands

Requires Windows with the **Visual Studio extension development workload**.

Open the solution in Visual Studio (17.14+ or VS 2026):

```cmd
start VSNeo.slnx
```

Build from the command line (when `msbuild` is available, e.g., from a Visual Studio Developer Command Prompt):

```cmd
msbuild VSNeo.slnx -restore -p:Configuration=Debug
```

The produced VSIX is dropped at:

```
VSNeo_Extension\bin\Debug\net472\VSNeo_Extension.vsix
```

`dotnet build` is **not** sufficient for this project: it references the Visual Studio SDK and WPF assemblies that require MSBuild/Visual Studio.

CI builds run on GitHub Actions (`.github/workflows/build.yml`): pushes to `master` publish a CI build to the Open VSIX Gallery, and pushing a `v*` tag attaches the VSIX to a GitHub Release and updates the Visual Studio Marketplace listing (`madskristensen/publish-marketplace`, metadata in `vs-publish.json`). Marketplace auth is Entra OIDC, no PAT (global PATs are decommissioned 2026-12-01): the `publish` job runs in the `release` environment, matching the federated credential on the `vsneo-marketplace-publish` app registration (a Contributor member of the marketplace publisher), and the acquired Entra token goes where VsixPublisher's PAT would. The vsixmanifest `Identity Id` must stay marketplace-legal (`[A-Za-z0-9-]`, <63 chars) — new listings are validated, old ones were grandfathered. CI stamps the version from the run/tag, so `BumpVersion.ps1` is suppressed there (`$(CI)` check in the csproj) and remains local-only. `docs/publish-to-marketplace.md` has the end-to-end recipe for recreating this pipeline for another extension.

## Run and debug

Press **F5** in Visual Studio. This launches an experimental instance with `/rootsuffix Exp` and deploys the extension.

> **Deploy with F5, not from the command line.** `msbuild` builds the VSIX but does not install it into the experimental hive, and `VSIXInstaller` installs it but does not trigger the extension rescan, so the running instance can silently load a previous build.

If `nvim.exe` is not on `PATH`, set the environment variable before launching:

```cmd
set VSNEO_NVIM_PATH=C:\path\to\nvim.exe
```

Enable key-path tracing (Debug builds only):

```cmd
set VSNEO_TRACE_KEYS=1
```

This logs every key decision to `%TEMP%\vsneo.log`. It is compiled out of Release builds.

If F5 reports *"the startup project cannot be launched"*, the debug launch path did not resolve. Check with:

```cmd
msbuild VSNeo_Extension\VSNeo_Extension.csproj -getProperty:StartProgram
```

## Code style and conventions

- C# 14, nullable enabled. Prefer explicit null checks over null-forgiving operators.
- Comments explain *why* a choice was made, not what the next line does.
- The key path must do **zero I/O** and must not block. Read `VSNeo_ExtensionPackage.Session` late; never capture it in a constructor that runs on the key path.
- Use `RunAsync`, `dispatcher.BeginInvoke`, or `JoinableTaskFactory.SwitchToMainThreadAsync`. Never `JoinableTaskFactory.Run`, `.Result`, or `.Wait()` in startup or key paths.
- `VSTHRD001` suppressions appear where `DispatcherPriority.Input` is deliberately chosen over `SwitchToMainThreadAsync`; include the measured reason in the comment.
- UI-thread only fields are documented as such. Use `ThreadHelper.ThrowIfNotOnUIThread()` at entry points.
- Log writes from lifecycle code are fine; never add logging to the key path.
- Buffer/cursor columns: nvim uses UTF-8 byte offsets; VS uses UTF-16 character offsets. All conversion must go through `ColumnMapper`.

## Testing

There is currently **no automated test suite** in this repository. Validation is manual:

1. Press F5 to launch the experimental instance.
2. Open a code file and verify mode appears in the status bar ("VSNeo: connected").
3. Exercise normal-mode motions, operators, visual mode, the command line (`:` and `/`), and VS commands from Vim mappings (`gd`, `K`, `<leader>rn`, etc.).
4. Inspect `%TEMP%\vsneo.log` when behavior is unexpected.
5. Use `VSNEO_TRACE_KEYS=1` to verify whether a specific key reaches the extension.

When adding behavior, add focused manual scenarios rather than broad integration tests unless you can run the real VS + nvim stack.

## Packaging and deployment

- The extension is packaged as a `.vsix` by the `Microsoft.VSSDK.BuildTools` package.
- `source.extension.vsixmanifest` registers the package asset and the MEF component asset. Missing the MEF asset is a common silent failure: the extension installs and loads, but no keystrokes are intercepted.
- `Lua/vsneo.lua` is included in the VSIX as content and copied to the output directory so F5 and the packaged extension both find it.
- For local development, deploy only through F5. For distribution, build the Release VSIX and install it with `VSIXInstaller` on the target machine.

## Security considerations

- The extension launches `nvim.exe` from `PATH` or from the `VSNEO_NVIM_PATH` environment variable. Ensure the path points to a trusted Neovim binary.
- `vsneo.lua` and user-provided mappings can execute arbitrary Visual Studio commands via `vsneo.cmd(...)` and `vsneo.goto_cmd(...)`. This is by design and is not sandboxed.
- The extension runs inside the `devenv.exe` process with the user's privileges.
- A Windows job object with `KILL_ON_JOB_CLOSE` is used to terminate nvim when Visual Studio exits, preventing orphaned headless processes.

## Known landmines

Do not change these without reading the full explanations in `CLAUDE.md`:

1. **Do not switch the nvim transport to stdio.** .NET's `RedirectStandardInput/Output` creates synchronous anonymous pipes; nvim's libuv stdio needs overlapped handles and exits silently on Windows. Use the named-pipe path in `NvimRpcClient`.
2. **Do not add the MessagePack NuGet package.** Visual Studio loads its own `MessagePack.dll`; shipping a second copy causes MEF composition failures. `MsgPack.cs` owns the subset nvim needs.
3. **Do not create `BufferMirror` with `new`.** Always use `BufferMirror.ForDocument` to enforce one live mirror (and one writer) per nvim buffer.
4. **Do not ignore `nvim_buf_detach_event`.** nvim unhooks the channel on buffer unload/reload; mirrors that ignore it go deaf.
5. **A null `changedtick` is not a real edit.** It is an `'inccommand'` preview. `BufferMirror.OnRemoteLines` drops these.
6. **Byte vs char columns.** All column translation must go through `ColumnMapper`.

## Useful starting points

- To understand the startup flow: `VSNeo_Extension/VSNeo_ExtensionPackage.cs` -> `Nvim/NvimSession.cs` -> `Nvim/NvimRpcClient.cs`.
- To understand key routing: `Editor/VsNeoKeyProcessorProvider.cs` and `Editor/VsNeoCommandFilter.cs`.
- To understand buffer sync: `Editor/BufferMirror.cs`.
- To understand cursor/selection: `Editor/CursorSynchronizer.cs`.
- To understand the nvim companion: `Lua/vsneo.lua`.
- To debug: `%TEMP%\vsneo.log`, and enable `VSNEO_TRACE_KEYS=1`.
