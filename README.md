# VSNeo

Visual Studio keeps its editor. Neovim is the brain behind it.

Neovim owns Vim semantics: mode, motions, operators, registers, marks, macros,
the command line. Visual Studio owns rendering, IntelliSense, refactorings, and
undo. The two are kept in sync through a mirrored buffer.

## The invariant

The key handler decides swallow-vs-passthrough from a locally cached mode, with
no I/O. The *effect* of the key travels over RPC and lands a few milliseconds
later. Decision local and synchronous; effect remote and async. Every design
choice in this repo follows from that, including the ones that look odd.

Corollaries, all of them non-negotiable:

- `TextViewCreated` does bookkeeping only. Eager work there froze VS once.
- No `JoinableTaskFactory.Run`, no `.Result`, no `.Wait()` anywhere in the
  startup or key path. `RunAsync` only.
- Fallback is a session-level circuit breaker, never a per-keystroke retry.
  Half-swallowed input is worse than being switched off.

## Build

Requires Windows and the Visual Studio extension development workload. The
solution is in the `.slnx` format, so it needs Visual Studio 2022 17.14+ or
Visual Studio 2026.

    start VSNeo.slnx

F5 launches an experimental instance (`/rootsuffix Exp`) with the extension
deployed. Set `VSNEO_NVIM_PATH` if `nvim.exe` is not on PATH.

**Deploy with F5, not from the command line.** `msbuild` builds the VSIX but
does not install it into the experimental hive, and `VSIXInstaller` installs it
but does not trigger the extension rescan, so the running instance keeps loading
the previous build. Both failures are silent — the symptom is testing an old
build while believing it is new. If you must do it by hand, it takes
`VSIXInstaller /rootSuffix:Exp` *and* `devenv /rootsuffix Exp /updateconfiguration`.
Check for more than one copy under
`%LOCALAPPDATA%\Microsoft\VisualStudio\<hive>\Extensions` if behaviour looks
stale; Visual Studio loads whichever it finds first.

Set `VSNEO_TRACE_KEYS=1` in the environment that launches the experimental
instance to log every key decision and every command routed through the view.
It answers the only question the outside cannot: whether a key reached us at
all. Off by default, and compiled out of Release.

If F5 reports *"the startup project cannot be launched"*, the debug launch path
did not resolve. That message names the wrong problem: the startup project is
fine, `StartProgram` is empty. `src\VSNeo\VSNeo.csproj.user` is where the legacy
C# project system looks first and it is gitignored, so a fresh clone has none;
the `.csproj` carries a `VsInstallRoot` fallback for that case. Check with:

    msbuild src\VSNeo\VSNeo.csproj -getProperty:StartProgram

An empty result means point it at your own `devenv.exe` in a `.csproj.user`.

From the command line:

    msbuild VSNeo.slnx -restore -p:Configuration=Debug

The build drops `VSNeo.vsix` in `src\VSNeo\bin\Debug\`.

## Known landmine: nvim stdio and .NET pipes

`--embed` over `Process.RedirectStandardInput/Output` **does not work on
Windows**. .NET creates synchronous anonymous pipes; nvim's stdio is libuv-backed
and needs overlapped handles. nvim exits with code 1 roughly 100ms after start,
having written nothing to stdout and nothing to stderr. The identical request
succeeds over a shell pipe or a file, which makes the failure look like a bug in
your msgpack encoder for as long as you believe it.

`NvimRpcClient` therefore starts nvim with `--headless --listen \\.\pipe\<guid>`
and connects with `NamedPipeClientStream` using `PipeOptions.Asynchronous`.

## Known landmine: MessagePack version conflict (resolved)

Visual Studio loads its own `MessagePack.dll` in-process for internal RPC, and a
VSIX shipping a second copy fails as assembly load errors that surface as
unrelated MEF composition errors.

`Nvim/MsgPack.cs` owns the subset nvim's RPC needs — nil, bool, int, float, str,
bin, array, map, ext — so there is no package reference and the VSIX ships
`VSNeo.dll` alone. Adding the package back also returns eight transitive
assemblies (`System.Memory`, `System.Collections.Immutable`,
`System.Runtime.CompilerServices.Unsafe`, …) that conflict with VS internals more
readily than MessagePack itself. Don't.

## Milestones

1. **Mode and navigation.** Read-only mirror, `nvim_input` for motions, cursor
   applied back, mode in the status bar. No operators, no diff-back. Proves the
   whole loop at almost no risk. *(this scaffold)*
2. **Operators.** `nvim_buf_attach` + `on_lines`, changedtick suppression,
   `ITextUndoHistory` transactions. This is where the real work is.
3. **`ext_cmdline` adornment.** Real `:` and `/` with your own mappings.
4. **`ext_messages`, search highlights, `hlsearch`.**
5. **Opt-in config loading.** `vim.g.vsneo` is already set, so an init can
   branch on it the way vscode-neovim uses `vim.g.vscode`. Plugins that
   manipulate text will work; anything that draws its own UI will not, since
   there is no screen.

## Still missing in milestone 1

- `IsIntelliSenseActive()` returns false. Wire it to `ICompletionBroker` and
  `ISignatureHelpBroker` before anyone tries to accept a completion with `j`.
- Cursor is not yet read back from nvim. Subscribe to `win_viewport`, then map
  through `ColumnMapper` and set `ITextView.Caret`.
- `BufferMirror` replaces the whole buffer on every change. Correct, and slow on
  large files. Translate `e.Changes` into `nvim_buf_set_text` spans.
- VS global keybindings win before the key processor sees some chords. `Ctrl+[`
  is the classic casualty. Handle `IVsFilterKeys2.TranslateAcceleratorEx` or
  remove the conflicting bindings.
