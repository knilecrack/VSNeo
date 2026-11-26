# Statusline

A statusline is the thin bar under the text that shows the mode, the file,
the cursor position and whatever else you put there. In Vim and Neovim it is
set with two options, and VSNeo reads the same two:

- `laststatus` decides **whether** there is one.
- `statusline` decides **what is in it**.

Neovim computes the line from those options; Visual Studio draws it under
each editor. So the format is Vim's own: a `statusline` from the internet,
from your Neovim config, or written by a plugin works as it is.

Settings go in `~/.vsneorc` (Vimscript) or `~/.vsneorc.lua` (Lua), in your
user folder (`%USERPROFILE%`). Both are read when Visual Studio starts;
`:source ~/.vsneorc.lua` re-reads one without restarting.

## Turn it on

```vim
" ~/.vsneorc
set laststatus=2
```

```lua
-- ~/.vsneorc.lua
vim.o.laststatus = 2
```

Or live, from the command line: `:set laststatus=2`. `:set laststatus=0`
turns it off again.

| `laststatus` | What you get |
|---|---|
| `0` | No statusline (the default). The mode still shows as the badge in Visual Studio's status bar. |
| `2` | A statusline under every editor, including editors in floating windows. |
| `3` | One global statusline in Neovim. Not drawn by VSNeo yet: nothing appears. |

`:set laststatus?` always answers `0`. That is deliberate: Neovim's own
statusline would take a row from its window, and its window has to stay
exactly as tall as Visual Studio's editor for `L`, `M`, `<C-d>` and
scrolling to land on the right line. VSNeo keeps Neovim's at 0 and
remembers the value you asked for.

## The default line

With `laststatus=2` and no `statusline` of your own, VSNeo shows:

```
 NORMAL  RCT12.cpp [+]                                     165:77  Bot
```

The mode as a colored chip, the file name, `[+]` when there are unsaved
changes, `line:col`, and where you are in the file (`Top`, `Bot`, `All` or a
percentage).

## Your own line

`statusline` is a string of text and `%` items. Some of the useful ones:

| Item | Shows |
|---|---|
| `%f` | File path, `%t` file name only |
| `%m` | `[+]` when modified |
| `%r` | `[RO]` when read-only |
| `%y` | File type, as `[cpp]` |
| `%l` / `%c` | Line / column |
| `%L` | Number of lines |
| `%p%%` | Position as a percentage |
| `%P` | `Top`, `Bot`, `All` or a percentage |
| `%=` | Everything after this goes to the right edge |
| `%<` | Where to cut when the line is too long |
| `%#Group#` | Switch to a highlight group's colors; `%*` switches back |
| `%{expr}` | The value of a Vimscript expression |
| `%{%expr%}` | The same, and `%` items in the result are expanded too |

The full list is Neovim's `:help 'statusline'`.

A plain example:

```vim
" ~/.vsneorc - spaces must be escaped with a backslash in :set
set laststatus=2
set statusline=%f\ %m%=%y\ %l:%c\ %P
```

The same in Lua, where no escaping is needed:

```lua
-- ~/.vsneorc.lua
vim.o.laststatus = 2
vim.o.statusline = '%f %m%=%y %l:%c %P'
```

### The mode

VSNeo provides the colored mode chip from the default line as a function you
can use in your own:

```lua
vim.o.statusline = '%{%v:lua.vsneo.statusline_mode()%} %f %m%=%l:%c'
```

Its colors come from highlight groups you can override:

```lua
vim.api.nvim_set_hl(0, 'VSNeoStatusModeNormal', { fg = '#1e1e2e', bg = '#89b4fa', bold = true })
vim.api.nvim_set_hl(0, 'VSNeoStatusModeInsert', { fg = '#1e1e2e', bg = '#a6e3a1', bold = true })
```

The groups are `VSNeoStatusModeNormal`, `Insert`, `Visual`, `Replace`,
`Command`, `Pending` (operator-pending, after `d` or `c`) and `Terminal`.

### Colors

Each `%#Group#` uses that highlight group's foreground, background and bold.
The bar's own background, and any part without a group, use `StatusLine`:

```lua
vim.api.nvim_set_hl(0, 'StatusLine', { fg = '#c6d0f5', bg = '#292c3c' })
vim.api.nvim_set_hl(0, 'MyFile', { fg = '#8caaee', bold = true })
vim.o.statusline = '%#MyFile#%t%* %m%=%l:%c'
```

Groups without colors fall back to `StatusLine`, and when that has none,
to the editor's own colors.

### Lua functions

For anything beyond `%` items, build the line in a Lua function and let
`%{%...%}` call it. The function runs every time the line is refreshed and
returns a statusline string:

```lua
function _G.my_statusline()
  local name = vim.fn.expand('%:t')
  local clock = os.date('%H:%M')
  return ' ' .. name .. ' %m%=' .. clock .. ' '
end

vim.o.statusline = '%{%v:lua.my_statusline()%}'
```

A `%` in text you put into the line (a file name, a branch) must be doubled,
or it is read as an item: `name:gsub('%%', '%%%%')`.

## A ready-made LazyVim look

[`examples/statusline-lazyvim.lua`](../examples/statusline-lazyvim.lua)
recreates LazyVim's statusline without any plugins: the mode in a colored
section, the git branch, the last folders of the path with the file name in
bold, a dot for unsaved changes, and on the right the file type, position,
`line:col` and a clock. Colors are Catppuccin Frappé, with LazyVim's own
Tokyo Night in a comment.

```
 NORMAL   develop  openrct2/rct12/RCT12.cpp ●              cpp  16%  165:77   20:53
```

To use it, either copy the file's contents into `~/.vsneorc.lua`, or load it
from there:

```lua
-- ~/.vsneorc.lua
dofile([[C:/path/to/VSNeo/examples/statusline-lazyvim.lua]])
```

To change it, edit the copy:

- **Colors** are in one table near the top (`local c = { ... }`).
- **Icons and arrows** need a Nerd Font (next section). `local nerd = false`
  at the top switches to plain text.
- **Sections** are put together in `lazyvim_statusline()` at the bottom.

The git branch is looked up once per folder, in the background, and appears
with the next key. Git diff counts and error counts, which LazyVim also
shows, are left out: in VSNeo that information lives in Visual Studio (its
own status bar and Error List), not in Neovim.

## Icons and fonts

The line is drawn in the editor's font, at the editor's zoom, so Neovim's
`%=` arithmetic matches what you see. Powerline arrows (``, ``) and
icons (``, ``) are not in ordinary programming fonts. For those
characters only, VSNeo falls back to the first of these that is installed:

Symbols Nerd Font Mono, CaskaydiaMono Nerd Font Mono, CaskaydiaCove Nerd
Font Mono, JetBrainsMono Nerd Font Mono, FiraCode Nerd Font Mono, MesloLGS
Nerd Font Mono, Hack Nerd Font Mono, BlexMono Nerd Font Mono, SauceCodePro
Nerd Font Mono.

If the icons show as boxes, none of them is installed. Install one from
[nerdfonts.com](https://www.nerdfonts.com) (Symbols Nerd Font is the
smallest: icons only), or leave icons out of your line.

## Plugins (lualine and others)

Statusline plugins work by setting `statusline`, usually to a
`%{%v:lua...%}` call into the plugin, and VSNeo evaluates whatever is set.
Plugins load from `~/.vsneo/pack/<any>/start/<plugin>`, not from your
regular Neovim config (see the plugin section of
[`examples/vsneorc.lua`](../examples/vsneorc.lua)). Not tested
with lualine itself; two things to know if you try:

- lualine's `globalstatus = true` sets `laststatus=3`, which VSNeo does not
  draw yet. Use `globalstatus = false`.
- Sections that read git signs, LSP or diagnostics stay empty: Neovim has no
  language servers or git integration inside VSNeo.

## How it behaves

- **Which view.** Neovim has one window, so the line always describes the
  focused editor. The others keep the last line they showed, dimmed, like
  Vim's inactive statuslines; an editor that has not had focus since the
  line came on shows an empty, dimmed bar.
- **When it updates.** On cursor moves, mode changes, buffer switches,
  saves, macro recording, color scheme changes and a new `statusline`.
  Only a line that actually changed is redrawn. A clock or anything else time-based moves with the
  next key, not on its own.
- **Width.** The bar spans the whole editor, line numbers and scrollbar
  included, and `%=` fills that width.
- **Resizing.** The editor shrinks by one row when the line is turned on,
  and does not resize again while you type.
- **Unsaved changes.** `%m` and `[+]` follow Visual Studio: a save there
  (Ctrl+S, File > Save, auto-save) clears them.
- **When Neovim is gone.** If Neovim stops or VSNeo falls back to plain
  Visual Studio editing, the line disappears with it, as the mode badge
  does.
- **Errors.** If a `%{...}` expression fails, the error is reported once as
  a warning in the status bar, not on every key, and the line shows
  whatever the rest of it produced. Errors inside the line never touch
  `v:errmsg`: it still holds your own last error.

## What does not work

- `laststatus=3` (one global line): nothing is drawn yet.
- Mouse clicks on the line (`%@function@` items).
- `winbar` and `tabline`.
- Timers that redraw the line on their own: the line refreshes with keys
  and events.
