# ------------------ WORK IN PROGRESS ----------------------
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

## How is this different from VsVim?

VSNeo exists because of [VsVim](https://github.com/VsVim/VsVim) — it's the
reason Vim users stay in Visual Studio, and `.vsvimrc` mappings port over
nearly verbatim via `:Vsc`. Where VSNeo differs:

- **VsVim reimplements Vim in C#; VSNeo embeds a real, headless Neovim.**
  Operators, text objects, registers, macros and the command line are executed
  by Neovim itself, so there is no emulation layer to drift — `:%s/a/b/g`
  works end to end, with the preview and the undo transaction you expect.
- **Your Neovim plugins come along** — anything that lives in the
  buffer/motion layer (surround, commentary, text objects) loads from
  `~/.vsneo/pack`.
- **Your config is Vimscript/Lua**, sourced from `~/.vsneorc` /
  `~/.vsneorc.lua`, so mappings carry over from your real Neovim setup.

## What you get

- **Real Vim semantics** — modes, motions, operators, counts, registers, marks,
  macros, visual and replace mode — executed by Neovim against a mirrored
  buffer and applied back into Visual Studio as ordinary edits.
- **`.` and macros include what you typed.** Insert-mode text goes to Visual
  Studio, so VSNeo records it and replays it: `cwfoo<Esc>` then `.` repeats
  the whole change, and `qa`...`q` records the typing too.
- **One undo history: Visual Studio's.** `u` and `Ctrl-R` walk it, one press
  per Vim change (`c3wXYZ<Esc>` undoes in one `u`), so undoing a Roslyn
  rename or a format works as well. The undone text flashes briefly.
- **A real `:` and `/` command line**, drawn as a floating popup at the top of
  the window (the Ctrl+Q shape), with wildmenu completion. Long output
  (`:map`, `:messages`) opens in a pager with Vim's more-prompt keys.
- **Search that is Neovim's regex engine**, with matches highlighted in the
  editor, a `[3/12]` count beside the current match, and ticks on the
  scrollbar for matches and marks; yank flash on `TextYankPost`.
- **Folding is Visual Studio outlining, driven by Vim keys** — `za`, `zo`,
  `zc`, `zR`, `zM`, `zj`/`zk` work on the language service's regions, and
  `zf` creates a real collapsible region.
- **Navigation history is Visual Studio's** — `Ctrl-O`/`Ctrl-I` walk back and
  forward through F12, Find All References and error-list jumps, not only
  Vim motions.
- **Window management that understands tab groups** — `Ctrl-W h/j/k/l` moves
  between splits by frame geometry, `:split`/`:vsplit`/`:q` map to the Visual
  Studio commands that produce the same layout, `Ctrl-6` walks
  most-recently-used documents, `gb` jumps to any tab by letter label.
  `:e`, `:b`, `:bn`/`:bp` open and switch documents in Visual Studio.
- **A flash-style `s` jump** with letter labels over visible matches, and
  labels on `f`/`F`/`t`/`T` when a line holds several matches.
- **Visual Studio commands from mappings**: `:Vsc Some.Command` runs anything
  from Tools > Options > Keyboard, with Tab completion; `:Vsclist [pattern]`
  opens a fuzzy Seeky list of commands and their shortcuts, and Enter runs the
  selection. `:Sethandler` chooses Vim or Visual
  Studio ownership per key and mode. `:VSNeoToggle` (or the clickable mode
  badge) switches between Vim and plain VS input without restarting Neovim.
  `gd`, `gD`, `gi`, `gr`, `[d`, `]d`, `K`, `<leader>rn`, `<leader>ca`,
  `<leader>f` are pre-wired to Roslyn navigation and refactorings.
- **Register and mark peek** — `"` lists Neovim's registers and `` ` `` / `'`
  its marks, with one-line previews beside the caret, and a which-key style
  popup shows what a pending prefix can still become.
- **A Telescope-style picker, built in** — fuzzy file finder, live grep
  (plain/regex/fuzzy), workspace symbols, document outline, current-file
  lines and git-modified files with a real preview pane, powered by the
  native fff engine with frecency learning. Bound to
  `Ctrl+Shift+Alt+O/I/G/L/,/B/M/R` (files, grep, grep word, current file,
  symbols, outline, git modified, resume) - those work in insert mode too -
  plus `<leader>sf`/`sg`/`sw`/`ss` in normal mode. The engine runs in its own
  process, so it can never take Visual Studio down. Same engine and shared
  ranking database as the standalone SeekyVS extension - nothing else to
  install.
- **Insert mode stays Visual Studio's** — IntelliSense, snippets, Copilot and
  brace completion work untouched. Only `Esc`, `Ctrl-W` and `Ctrl-O` are
  claimed there, plus any insert mapping on a named key you define yourself
  (`imap <Down> <Esc><Down>`) or explicitly assign with `:Sethandler`.
- **Relative line numbers and a mode badge** that follow `'number'` and
  `'relativenumber'`, with the mode shown in the status bar.
- **Cursor, motion and effects** — a Neovide-style cursor trail, particle and
  cyberpunk effects, a custom cursor with per-mode colors and glow, smooth
  scrolling, a jump beacon, a mode-colored cursor line and presets (Blade
  Runner, The Matrix, Cyberpunk 2077). All of it, with previews:
  [`docs/visual-features.md`](docs/visual-features.md).

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

VSNeo sources `%USERPROFILE%\.vsneorc` (Vimscript), then
`%USERPROFILE%\.vsneorc.lua` (Lua), at startup if they exist.
[`examples/vsneorc.vim`](examples/vsneorc.vim) is a full `.vsvimrc` ported to
VSNeo, and [`examples/vsneorc.lua`](examples/vsneorc.lua) shows every option
and the effect presets. Every setting, command and default mapping is listed
in [`docs/options.md`](docs/options.md). Two porting notes:

- `inoremap` on *printable* keys can never fire — typed characters go to Visual
  Studio, not Neovim. Insert mappings on *named* keys work: `imap <Left> <Esc>`
  and friends are claimed from Visual Studio and executed by Neovim itself
  (string, Lua-callback, and expr rhs all supported). Only mappings from your
  own rc are claimed — Neovim's defaults and everyone else's insert mode are
  untouched.
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
extension development** workload, the .NET 10 SDK (for the Seeky picker's
engine), and Neovim on `PATH`.

```cmd
git clone --recurse-submodules https://github.com/knilecrack/VSNeo.git
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

Tests: `pwsh tests/run-tests.ps1` runs the Lua companion suites against a
headless Neovim. `dotnet test tests/dotnet/VSNeo.Tests` runs the C# unit
tests (msgpack codec, stream framing, UTF-8 ↔ UTF-16 columns, the nvim →
Visual Studio line edits) on .NET 8 — no Visual Studio needed, so they run
on Linux and macOS too.

The Seeky picker's shared code comes from the `external/Seeky` submodule
(clone with `--recurse-submodules`, or run `git submodule update --init`).
Building it, taking changes from the Seeky repo and installing a local build
into your normal Visual Studio: [`docs/seeky.md`](docs/seeky.md) and
[`docs/seeky-development.md`](docs/seeky-development.md).

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
