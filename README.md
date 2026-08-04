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

Requires Windows, Visual Studio 2022 17.9+, and the Visual Studio extension
development workload. Then:

    git init
    start VSNeo.sln

F5 launches an experimental instance (`/rootsuffix Exp`) with the extension
deployed. Set `VSNEO_NVIM_PATH` if `nvim.exe` is not on PATH.

## Known landmine: MessagePack version conflict

Visual Studio loads its own `MessagePack.dll` in-process for internal RPC. If
this VSIX ships a different major version you get assembly load failures that
look like unrelated MEF composition errors. Check what VS 17.x ships in
`Common7\IDE\PublicAssemblies` and `PrivateAssemblies`, and pin to it.

If that turns into a fight, the escape hatch is to drop the dependency
entirely. Neovim's RPC needs only a small subset of msgpack — int, str, bin,
array, map, bool, nil, ext — which is roughly 200 lines hand-rolled and zero
version risk inside a VSIX.

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
