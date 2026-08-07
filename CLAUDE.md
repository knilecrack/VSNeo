# VSNeo — working context

Visual Studio keeps its editor. Neovim is the brain behind it.

Neovim owns Vim semantics (mode, motions, operators, registers, marks, macros,
command line). Visual Studio owns rendering, IntelliSense, refactorings, and
undo. They are kept in sync through a mirrored buffer.

This is deliberately **not** the "render nvim in a window" approach. We consume
`nvim_ui_attach` as a *state feed* and let VS keep drawing the text, so Roslyn
features survive. Do not propose embedding a grid renderer or reparenting
Neovide — both were considered and rejected.

## The invariant everything follows from

**The key handler decides swallow-vs-passthrough from a locally cached mode,
with zero I/O. The effect of the key travels over RPC and lands a few
milliseconds later.** Decision local and synchronous; effect remote and async.

Non-negotiable corollaries:

- `TextViewCreated` does bookkeeping only. Eager work there froze VS once
  already. No service resolution, no process start, no RPC.
- No `JoinableTaskFactory.Run`, no `.Result`, no `.Wait()` in the startup path
  or the key path. `RunAsync` only.
- Fallback is a session-level circuit breaker, never a per-keystroke retry.
  Half-swallowed input leaves the buffers drifting and is worse than being off.
- Insert mode passes through to VS untouched so IntelliSense, snippets, and
  brace completion keep working. `<Esc>` is the only key claimed in insert.

## Constraints

- VSIX, .NET Framework 4.8. This is fixed: in-process extensions share
  `devenv.exe`, which is Framework-based. Running out-of-process is not an
  option — the new VisualStudio.Extensibility model has no keyboard
  interception surface, and it would add a second RPC hop per keystroke.
- In-proc VisualStudio.Extensibility *can* be added alongside the MEF parts for
  commands/settings/tool windows. It is additive, planned for milestone 3, and
  changes nothing about the key path.
- Target VS 2022 (17.x) and VS 2026 (18.x).

## Layout

SDK-style VSIX project, net472. The earlier hand-written `src/VSNeo/VSNeo.csproj`
is gone: it lacked the project-type GUIDs, so F5 refused to launch it.

    VSNeo_Extension/
      VSNeo_ExtensionPackage.cs  AsyncPackage, background load, owns nvim lifetime
      Nvim/MsgPack.cs            hand-rolled msgpack: reader, writer, stream framer
      Nvim/NvimRpcClient.cs      msgpack-rpc over a named pipe ([0,id,method,params])
      Nvim/NvimSession.cs        attach/activate split, nvim_input, ui_attach, Lua companion
      Nvim/NvimLua.cs            the Lua that runs inside nvim, free of SDK deps so it can be tested
      Nvim/NvimStateHub.cs       companion rpcnotify -> cached mode + cursor; redraw -> cmdline
      Editor/VsNeoKeyProcessorProvider.cs   the synchronous decision point (WPF keys)
      Editor/VsNeoCommandFilter.cs          IOleCommandTarget, for keys VS took first
      Editor/IntelliSenseGate.cs            is VS's own UI owed this keystroke?
      Editor/KeyEncoder.cs       WPF keys -> nvim notation
      Editor/BufferMirror.cs     VS -> nvim: one nvim buffer per document, edits as spans
      Editor/CursorSynchronizer.cs          both directions, off the key path
      Editor/ViewportSynchronizer.cs        grid size + topline, for <C-d>/H/M/L/zz
      Editor/TextViewCreationListener.cs    bookkeeping only, see invariant
      Infrastructure/CircuitBreaker.cs
      Infrastructure/ProcessJob.cs          KILL_ON_JOB_CLOSE, so nvim cannot orphan
      Infrastructure/ColumnMapper.cs        byte <-> char, single source of truth
      Infrastructure/Log.cs                 lifecycle diagnostics -> %TEMP%\vsneo.log

**Two interception points, by necessity.** The KeyProcessor sees WPF key events;
anything Visual Studio has already turned into a command never reaches it.
`Escape` is the case that proves it - VS routes it as VSStd2K `CANCEL` through
`IOleCommandTarget`, so `PreviewKeyDown` is never called for it. `Ctrl+[` is the
same story. Characters and chords go through the KeyProcessor, commands through
`VsNeoCommandFilter`.

## Visual Studio commands from Vim mappings

The reason for keeping VS as the editor, and the thing neither tool gives you
alone. `vsneo.cmd(name)` runs any command by the name in
Tools > Options > Keyboard; `vsneo.goto_cmd(name)` does the same after recording
the jump, so `<C-o>` comes back from it.

    vim.keymap.set('n', '<leader>b', function() vsneo.cmd('Build.BuildSolution') end)

Defaults wire `gd`, `gD`, `gi`, `gr`, `[d`, `]d` to Roslyn's navigation, and `K`,
`<leader>rn`, `<leader>ca`, `<leader>f` to quick info, rename, quick actions and
format. Vim's own `gd` is a same-file text search and is strictly worse here.

## Milestones

1. **Mode and navigation** — read-only mirror, `nvim_input` for motions, cursor
   applied back, mode in status bar. No operators. *(current)*
2. **Operators** — `nvim_buf_attach` + `on_lines` applied back into VS.
   *(in progress: edits land, undo grouping still to do)*
3. **`ext_cmdline` adornment** — real `:` and `/`.
4. **`ext_messages`, search highlights, `hlsearch`.**
5. **Opt-in config loading** — `vim.g.vsneo` is already set. Text-manipulating
   plugins will work; anything drawing its own UI will not.

## Open work in milestone 1

- `Ctrl+F` is still VS's Find. Deliberate: Vim's replacement is `/`, which has
  no UI until `ext_cmdline` lands in milestone 3. Add it to
  `KeyBindingCleaner.Chords` then, not before.
- `KeyBindingCleaner` unbinds through DTE, and those writes only reach disk on a
  clean shutdown - a killed instance loses them and the chord is bound again
  next launch. Removing a binding by hand in Tools > Options > Keyboard
  persists properly. Worth revisiting if it keeps biting.
- `ViewportSynchronizer` cannot represent a VS viewport scrolled away from the
  caret: an nvim window always contains its own cursor, so a topline that would
  hide it is refused. Topline is only pushed while the caret is visible. Fine in
  practice, but `H`/`M`/`L` are stale after a wheel-scroll until you click.
- Drift between the two buffers is repaired by comparing them 500ms after
  editing stops (`BufferMirror.Verify`). Now a safety net rather than the
  mechanism, since `on_lines` applies nvim's edits directly - but it still makes
  VS authoritative, so it would undo an nvim edit that failed to apply.
- Edits from nvim are not yet grouped into `ITextUndoHistory` transactions, so
  one operator can be several undo steps in VS. `J` and `cw` each emit two
  `on_lines` events, which is what makes this visible.
- Echo suppression compares text rather than tracking changedtick. The tick
  identifying our own write arrives on a later *reply* than the notification it
  belongs to, so a tick-based check races; comparing is free of that.

## Known landmines

- **nvim stdio does not work from .NET on Windows.** `Process` with
  `RedirectStandardInput/Output` creates *synchronous* anonymous pipes. nvim's
  stdio is libuv-backed and wants overlapped handles, so `--embed` over redirected
  stdio dies ~100ms after start: exit code 1, empty stderr, not one byte written.
  The same nvim answers perfectly over a shell pipe or a file, so this reads as a
  bug in your encoder for as long as you let it. Fixed by `--listen` onto a named
  pipe plus `NamedPipeClientStream` with `PipeOptions.Asynchronous`. Do not
  "simplify" this back to stdio.

- **MessagePack version conflict.** *(resolved - keep it that way.)* VS loads its
  own `MessagePack.dll` in-process, and a VSIX that ships a second copy fails as
  unrelated MEF composition errors that never mention MessagePack. `Nvim/MsgPack.cs`
  now owns the subset nvim needs, so the VSIX ships `VSNeo.dll` and nothing else.
  Reintroducing the package also drags back eight transitive assemblies
  (`System.Memory`, `System.Collections.Immutable`, `System.Runtime.CompilerServices.Unsafe`
  and friends) that collide with VS internals far more readily than MessagePack does.
- **Byte vs char columns.** nvim columns are UTF-8 byte offsets; VS wants UTF-16
  char offsets. All conversion lives in `ColumnMapper`. Test with emoji and
  accented Latin early.
- **Undo ownership.** Two undo stacks is unwinnable. VS's `ITextUndoHistory` is
  authoritative; intercept `u` and `Ctrl-R` as special cases and resync nvim
  after. We give up nvim's undo tree to keep undo-a-Roslyn-rename working.
- **VS global keybindings** win before the key processor sees some chords.
  `Ctrl+[` is the classic casualty. Handle
  `IVsFilterKeys2.TranslateAcceleratorEx` or remove the conflicting bindings.

## Build

Windows, extension development workload. The solution is `.slnx`, so VS 2022
17.14+ or VS 2026 — earlier 17.x cannot open it.
F5 launches an experimental instance (`/rootsuffix Exp`).
Set `VSNEO_NVIM_PATH` if `nvim.exe` is not on PATH.

    msbuild VSNeo.slnx -restore -p:Configuration=Debug
