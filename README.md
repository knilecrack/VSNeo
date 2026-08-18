# VSNeo

Visual Studio keeps its editor. Neovim is the brain behind it.

Neovim owns Vim semantics: mode, motions, operators, registers, marks, macros,
the command line. Visual Studio owns rendering, IntelliSense, refactorings, and
undo. The two are kept in sync through a mirrored buffer.

VSNeo is an in-process Visual Studio extension that embeds a headless Neovim
instance (msgpack-rpc over a named pipe) and forwards Vim semantics into the
standard Visual Studio text editor — not a reimplementation of Vim in C#, and
not a terminal embedded in a tool window.

## What works today

- Modes, motions, operators, counts, registers, marks, macros, visual mode —
  executed by real Neovim against a mirrored buffer, applied back into Visual
  Studio's undo history as proper transactions.
- A real `:` and `/` command line, drawn as a floating noice-style popup with
  wildmenu completion. `:%s/a/b/g` works end to end.
- Search highlights computed by Neovim's regex engine (Vim syntax unchanged),
  drawn in the editor, current match in `CurSearch`; yank flash on
  `TextYankPost`.
- Window management that understands Visual Studio's tab groups:
  - `Ctrl-W h/j/k/l` — directional focus between splits, resolved from frame
    geometry, landing on the tab you last used in that group.
  - `Ctrl-W s/v/w/q/c`, `:split`/`:vsplit`/`:q` — mapped to the Visual Studio
    commands that produce the same layout.
  - `Ctrl-6` — alternate-file toggle; repeated presses walk the
    most-recently-used documents (Ctrl+Tab semantics without the popup).
  - `gb` — labeled jump to any open tab (PeasyMotion-style letter overlays on
    the tab well).
- Flash-style `s` jump with letter labels over visible matches, and labels on
  `f`/`F`/`t`/`T` when the line holds several matches (`;` and `,` keep
  working).
- Visual Studio commands from mappings: `:Vsc Some.Command` runs anything from
  Tools > Options > Keyboard, so VsVim-style `.vsvimrc` mappings port nearly
  verbatim. `gd`, `gD`, `gi`, `gr`, `[d`, `]d`, `K`, `<leader>rn`,
  `<leader>ca`, `<leader>f` are pre-wired to Roslyn navigation and refactorings.
- User config in `~/.vsneorc` (vimscript), sourced at startup — see
  `examples/vsneorc.vim` for a full ported `.vsvimrc`.
- Opt-in plugins under `~/.vsneo/pack/<group>/{start,opt}` — plugins that live
  in the buffer/motion layer (surround, commentary, text objects) work; UI
  plugins draw to a grid nothing displays and cannot.
- Relative line numbers, floating command line, messages margin.
- Insert mode belongs to Visual Studio: IntelliSense, snippets, Copilot,
  brace completion keep working natively. Only `Esc` (and `Ctrl-W` delete-word)
  are claimed there.

## Installation

VSNeo has three parts, only one of which is strictly required beyond the
extension itself:

| What | Where | Required? |
| --- | --- | --- |
| The extension | `VSNeo_Extension.vsix`, installed into Visual Studio | yes |
| Neovim | `nvim.exe` on `PATH`, or anywhere via `VSNEO_NVIM_PATH` | yes |
| Your config | `%USERPROFILE%\.vsneorc` | no |
| Your plugins | `%USERPROFILE%\.vsneo\pack\...` | no |

**1. Install Neovim.** Any recent build from
[neovim.io](https://neovim.io) works (developed against 0.12). Put `nvim.exe`
on `PATH`, or set the environment variable before launching Visual Studio:

    set VSNEO_NVIM_PATH=C:\path\to\nvim.exe

**2. Install the extension.** There is no marketplace release yet; build from
source. You need the **Visual Studio extension development** workload, and the
solution is in the `.slnx` format, so opening it takes Visual Studio 2022
17.14+ or Visual Studio 2026.

    start VSNeo.slnx

F5 launches an experimental instance (`/rootsuffix Exp`) with the extension
deployed — the normal way to develop or try it. For a regular install into
your main instance, build Release and run `VSIXInstaller` on the result:

    msbuild VSNeo.slnx -restore -p:Configuration=Release
    :: produces VSNeo_Extension\bin\Release\net472\VSNeo_Extension.vsix

The extension's companion script (`Lua/vsneo.lua`) ships inside the VSIX —
nothing to copy by hand. After install, open a code file and look for
"VSNeo: connected" in the status bar.

**3. Optional: your config.** VSNeo sources `%USERPROFILE%\.vsneorc`
(vimscript) at startup if it exists. `examples/vsneorc.vim` in this repo is a
full `.vsvimrc` ported to VSNeo — copy it there and restart Visual Studio.
See *Configuring* below for what does and does not port.

**4. Optional: plugins.** Opt-in through the standard packages layout rooted
at `%USERPROFILE%\.vsneo`: `pack\<group>\start\<plugin>` loads at startup,
`pack\<group>\opt\<plugin>` is `:packadd`-able from the rc. Your regular nvim
plugins are deliberately not loaded. Only plugins that live in the
buffer/motion layer can work (surround, commentary, text objects); UI plugins
render to a grid nothing displays.

**Deploy with F5, not from the command line.** `msbuild` builds the VSIX but
does not install it into the experimental hive, and `VSIXInstaller` installs it
but does not trigger the extension rescan, so the running instance keeps
loading the previous build. Both failures are silent — the symptom is testing
an old build while believing it is new. If you must do it by hand, it takes
`VSIXInstaller /rootSuffix:Exp` *and* `devenv /rootsuffix Exp /updateconfiguration`.
Check for more than one copy under
`%LOCALAPPDATA%\Microsoft\VisualStudio\<hive>\Extensions` if behaviour looks
stale; Visual Studio loads whichever it finds first.

## Configuring

After the companion script sets everything up, it sources `~/.vsneorc`
(vimscript) if it exists, then re-asserts the sync-critical options (`wrap`,
`scrolloff`, `laststatus`, `swapfile`) the viewport and buffer mirror rely on.

Two porting notes:

- `inoremap` entries can never fire: insert-mode keys never reach Neovim.
- `Ctrl+Alt(+Shift)` chords are never claimed — that is AltGr on many layouts
  and Visual Studio's own binding namespace.

## Troubleshooting

Lifecycle diagnostics go to `%TEMP%\vsneo.log`. Set `VSNEO_TRACE_KEYS=1` in the
environment that launches the experimental instance to log every key decision
and every command routed through the view. It answers the only question the
outside cannot: whether a key reached us at all. Off by default, and compiled
out of Release.

If F5 reports *"the startup project cannot be launched"*, the debug launch path
did not resolve. That message names the wrong problem: the startup project is
fine, `StartProgram` is empty. Check with:

    msbuild VSNeo_Extension\VSNeo_Extension.csproj -getProperty:StartProgram

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

## Known landmine: nvim stdio and .NET pipes

`--embed` over `Process.RedirectStandardInput/Output` **does not work on
Windows**. .NET creates synchronous anonymous pipes; nvim's stdio is libuv-backed
and needs overlapped handles. nvim exits with code 1 roughly 100ms after start,
having written nothing to stdout and nothing to stderr. The identical request
succeeds over a shell pipe or a file, which makes the failure look like a bug in
your msgpack encoder for as long as you believe it.

`NvimRpcClient` therefore starts nvim with `--headless --listen \\.\pipe\<guid>`
and connects with `NamedPipeClientStream` using `PipeOptions.Asynchronous`.

## Known landmine: MessagePack version conflict

Visual Studio loads its own `MessagePack.dll` in-process for internal RPC, and a
VSIX shipping a second copy fails as assembly load errors that surface as
unrelated MEF composition errors.

`Nvim/MsgPack.cs` owns the subset nvim's RPC needs — nil, bool, int, float, str,
bin, array, map, ext — so there is no package reference and the VSIX ships
`VSNeo.dll` alone. Adding the package back also returns eight transitive
assemblies (`System.Memory`, `System.Collections.Immutable`,
`System.Runtime.CompilerServices.Unsafe`, …) that conflict with VS internals more
readily than MessagePack itself. Don't.

## Status

The original milestone list is complete: mode and navigation, operators with
undo transactions, `ext_cmdline`, `ext_messages` + search highlights, and
opt-in config loading all shipped. Current limits are documented in
`CLAUDE.md` under "Open work and known issues" (blockwise-visual `$`
highlight, `msg_showcmd` not rendered, and similar).
