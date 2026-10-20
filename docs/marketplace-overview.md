# VSNeo — a real Neovim inside Visual Studio

**Not a Vim emulator.** VSNeo runs your own `nvim.exe` headless inside Visual Studio and lets it drive the standard editor. Every motion, operator, text object, register, macro and `:command` is executed by Neovim itself. Visual Studio keeps what it is best at: rendering, IntelliSense, Roslyn refactorings, Copilot and undo.

![Cursor styles and effects](https://raw.githubusercontent.com/knilecrack/VSNeo/master/docs/cursor-vfx/cursor-styles.gif)

<!-- TODO: replace with a 10 s editor demo: ci" + . + :%s + gd. -->

## Why people switch from VsVim

| | VsVim | VSNeo |
|---|---|---|
| Vim engine | Reimplemented in C# | The real Neovim, over msgpack-rpc |
| `:%s/a/b/g`, `:g`, `:normal`, counts, text objects | Subset | Everything Neovim does |
| `.` and macros after insert-mode typing | Yes | Yes, including what you typed |
| Neovim plugins (surround, commentary, text objects) | No | Yes, from `~/.vsneo/pack` |
| Config | `.vsvimrc` | Your real `init.lua` / Vimscript (`~/.vsneorc`, `~/.vsneorc.lua`) |
| `.vsvimrc` mappings | | Port nearly verbatim via `:Vsc` |

## What you get

- **Real Vim semantics** — modes, motions, operators, counts, registers, marks, macros, visual and replace mode, executed by Neovim and applied back into Visual Studio as ordinary edits.
- **One undo history: Visual Studio's.** `u` undoes one Vim change at a time, including a Roslyn rename or a format.
- **A floating `:` and `/` command line** with wildmenu completion, and a pager for long output.
- **Neovim's regex search**, highlighted in the editor with a `[3/12]` counter and scrollbar ticks.
- **Folding is Visual Studio outlining** driven by `za`, `zo`, `zc`, `zR`, `zM`, `zf`.
- **`gd`, `gr`, `gi`, `K`, `<leader>rn`, `<leader>ca`** pre-wired to Roslyn navigation and refactorings. `Ctrl-O`/`Ctrl-I` walk Visual Studio's navigation history.
- **Splits and tabs** — `Ctrl-W h/j/k/l`, `:split`, `:vsplit`, `:e`, `:b`, `gb` to jump to any tab by letter.
- **A Telescope-style fuzzy picker, built in** — files, live grep, symbols, document outline, git-modified files, with a preview pane and frecency ranking. Works in insert mode too.
- **Flash-style `s` jump** with letter labels, register and mark peek, a which-key popup for pending prefixes.
- **Insert mode stays Visual Studio's.** IntelliSense, snippets, Copilot and brace completion are untouched.
- **Looks** — Neovide-style cursor trail, custom per-mode cursors, smooth scrolling, relative line numbers, and one-command presets: `:VSNeoPreset tokyo_night`, `catppuccin_mocha`, `blade_runner`, `matrix`, `cyberpunk2077`. [Previews and every option.](https://github.com/knilecrack/VSNeo/blob/master/docs/visual-features.md)

## Requirements

- Windows, Visual Studio 2022 **17.14+** or Visual Studio 2026.
- [Neovim](https://neovim.io) on `PATH` (or `VSNEO_NVIM_PATH`). Developed against 0.12.

## 60-second start

1. Install, open any code file, look for **VSNeo: connected** in the status bar.
2. `gg`, `%`, `ci"`, `vap`, `.` behave exactly as in Vim.
3. `:` and `/` open the floating command line. `Tab` completes.
4. Map anything: `nnoremap <leader>b :Vsc Build.BuildSolution<CR>` in `~/.vsneorc`.

Full configuration, porting notes from `.vsvimrc`, plugin loading and troubleshooting: [README on GitHub](https://github.com/knilecrack/VSNeo#configuration). Bugs and ideas: [issues](https://github.com/knilecrack/VSNeo/issues). MIT licensed.
