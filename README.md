# --------------------------------- WORK IN PROGRESS ---------------------------------
# VSNeo — Neovim for Visual Studio

Visual Studio keeps its editor. Neovim is the brain behind it.

VSNeo embeds a real, headless Neovim inside Visual Studio and lets it drive the
standard editor: modes, motions, operators, registers, marks, macros and the
command line are executed by Neovim itself, while Visual Studio keeps
rendering, IntelliSense, refactorings and undo. Not a C# reimplementation of
Vim, and not a terminal in a tool window — the same `nvim.exe` you already
have, speaking msgpack-rpc over a named pipe.

<!-- TODO: demo GIF here — one % toggle, one :%s, one gd. Storefronts live on this. -->

[![build](https://github.com/knilecrack/VSNeo/actions/workflows/build.yml/badge.svg)](https://github.com/knilecrack/VSNeo/actions/workflows/build.yml)
[![license: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE.txt)

## Why not VsVim / a Vim emulation?

Because emulation drifts. Every Vim-behaving layer eventually has to
re-implement operators, text objects, `'/` register quirks and the command
line, and every one of them gets it almost right. VSNeo runs the real thing:
if Neovim does it, VSNeo does it — including `:%s/a/b/g` end to end, with the
preview and the undo transaction you expect. And because Visual Studio remains
the editor, `gd` is Roslyn's Go To Definition across projects, not a same-file
text search.

## What you get

- **Real Vim semantics** — modes, motions, operators, counts, registers, marks,
  macros, visual mode — executed by Neovim against a mirrored buffer and
  applied back into Visual Studio's undo history as proper transactions.
- **A real `:` and `/` command line**, drawn as a floating popup at the top of
  the window (the Ctrl+Q shape), with wildmenu completion.
- **Search that is Neovim's regex engine**, with matches highlighted in the
  editor and the current match in `CurSearch`; yank flash on `TextYankPost`.
- **Window management that understands tab groups** — `Ctrl-W h/j/k/l` moves
  between splits by frame geometry, `:split`/`:vsplit`/`:q` map to the Visual
  Studio commands that produce the same layout, `Ctrl-6` walks
  most-recently-used documents, `gb` jumps to any tab by letter label.
- **A flash-style `s` jump** with letter labels over visible matches, and
  labels on `f`/`F`/`t`/`T` when a line holds several matches.
- **Visual Studio commands from mappings**: `:Vsc Some.Command` runs anything
  from Tools > Options > Keyboard, so `.vsvimrc` mappings port nearly
  verbatim. `gd`, `gD`, `gi`, `gr`, `[d`, `]d`, `K`, `<leader>rn`,
  `<leader>ca`, `<leader>f` are pre-wired to Roslyn navigation and
  refactorings.
- **Register peek** — `"` lists Neovim's registers with previews beside the
  caret.
- **Insert mode stays Visual Studio's** — IntelliSense, snippets, Copilot and
  brace completion work untouched. Only `Esc` and `Ctrl-W` are claimed there.
- **Your config and (some of) your plugins** — `~/.vsneorc` is sourced at
  startup, and buffer/motion-layer plugins (surround, commentary, text
  objects) load from `~/.vsneo/pack/<group>/{start,opt}`.

## Requirements

- Windows, Visual Studio 2022 **17.14+** or Visual Studio 2026.
- [Neovim](https://neovim.io) — any recent build (developed against 0.12),
  `nvim.exe` on `PATH` or pointed at with `VSNEO_NVIM_PATH`.

## Install

From the [Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=knilecrack.VSNeo),
or grab the VSIX from a
[GitHub Release](https://github.com/knilecrack/VSNeo/releases) / the CI build
on the [Open VSIX Gallery](https://www.vsixgallery.com/extension/VSNeo-ec21d06f-8395-403c-99b0-f2c417442403).

Then open a code file and look for **VSNeo: connected** in the status bar.

## Quickstart

1. Open any source file. You start in normal mode — the caret is a block.
2. `gg`, `%`, `ci"`, `vap`, `.` — everything behaves as in Vim.
3. `:` and `/` open the floating command line; `Tab` completes.
4. `:Vsc Edit.GoToBrace`, or map it: `nnoremap [{ :Vsc Edit.GotoBrace<CR>` in
   `~/.vsneorc`.

## Configuration

VSNeo sources `%USERPROFILE%\.vsneorc` (vimscript) at startup if it exists.
[`examples/vsneorc.vim`](examples/vsneorc.vim) is a full `.vsvimrc` ported to
VSNeo — copy it and restart Visual Studio. Two porting notes:

- `inoremap` can never fire — insert-mode keys go to Visual Studio, not Neovim.
- `Ctrl+Alt(+Shift)` chords are never claimed — that is AltGr on many layouts
  and Visual Studio's own binding namespace.

Plugins live under `%USERPROFILE%\.vsneo\pack\<group>\start\<plugin>` (loaded
at startup) or `...\opt\<plugin>` (`:packadd`-able). Your regular Neovim
plugins are deliberately not loaded; UI plugins draw to a grid nothing
displays and cannot work here.

## Troubleshooting

- Lifecycle diagnostics go to `%TEMP%\vsneo.log` — check there first.
- `set VSNEO_TRACE_KEYS=1` before launching Visual Studio logs every key
  decision (Debug builds only). It answers the only question the outside
  cannot: did the key reach the extension at all.
- If Neovim isn't found, set `VSNEO_NVIM_PATH=C:\path\to\nvim.exe`.

## Development

You need Windows, Visual Studio 2022 17.14+ or 2026 with the **Visual Studio
extension development** workload, and Neovim on `PATH`.

```cmd
git clone https://github.com/knilecrack/VSNeo.git
cd VSNeo
start VSNeo.slnx
```

Press **F5** to launch an experimental instance (`/rootsuffix Exp`) with the
extension deployed. Deploy with F5, not from the command line: `msbuild`
builds the VSIX but doesn't install it, and `VSIXInstaller` doesn't trigger
the extension rescan — both fail silently and you test an old build.

Command-line build (from a *Developer Command Prompt*, where `msbuild` is on
`PATH`):

```cmd
msbuild VSNeo.slnx -restore -p:Configuration=Debug
```

`dotnet build` is not sufficient — the project references the Visual Studio
SDK and WPF assemblies that require MSBuild.

Design rationale, the key-path invariant and the known landmines live in
[`CLAUDE.md`](CLAUDE.md); [`AGENTS.md`](AGENTS.md) is the contributor/agent
guide with the project layout and conventions.

## How it works, in one paragraph

A `KeyProcessor` and an `IOleCommandTarget` filter intercept keystrokes and
decide swallow-vs-passthrough from a locally cached mode — zero I/O on the key
path. The effect travels over msgpack-rpc to a headless Neovim
(`--headless --listen \\.\pipe\vsneo-<guid>`), which edits a mirrored buffer;
buffer, cursor, viewport, mode, command line and search state stream back as
RPC notifications and are applied to the Visual Studio editor. A companion
Lua script (`Lua/vsneo.lua`) inside Neovim pushes the state the redraw
protocol doesn't carry.

## License

[MIT](LICENSE.txt)
