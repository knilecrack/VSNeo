# VSNeo options

Every setting VSNeo reads, in one place. Most live in your Neovim config for
VSNeo; a few are Visual Studio settings or environment variables.

## Where settings go

| File | Read | Notes |
|---|---|---|
| `~/.vsneorc` | at startup, first | Vimscript. |
| `~/.vsneorc.lua` | at startup, after `~/.vsneorc` | Lua. Mappings with `desc` (shown by the which-key popup) belong here. |

`~` is your user profile (`%USERPROFILE%`). `:source ~/.vsneorc.lua` applies
every `vsneo_*` setting below live, no restart. A worked example of every
option, plus presets, is in [`examples/vsneorc.lua`](../examples/vsneorc.lua).
What the cursor, motion and effect options look like, with previews:
[`visual-features.md`](visual-features.md).

Settings are `vim.g` variables (`let g:...` in Vimscript). Times are in
seconds, like Neovide's; VSNeo uses Neovide's names with a `vsneo_` prefix, so
a Neovide config ports by renaming.

## Do not disturb

| Option | Default | |
|---|---|---|
| `vim.g.vsneo_dnd` | `false` | `true` turns every animation and effect off at once: trail, effects, smooth scrolling, beacon, custom cursor, glow, mode line. Just Visual Studio's plain caret. Your other settings are kept and come back when it is off. |

`:VSNeoDnd` toggles it live, `:VSNeoDnd on` / `:VSNeoDnd off` set it. A mapping:

```lua
vim.keymap.set('n', '<leader>z', '<Cmd>VSNeoDnd<CR>', { desc = 'Focus: animations off/on' })
```

## Cursor trail

Neovide's cursor animation: the cursor smears from where it was to where it is.

| Option | Default | |
|---|---|---|
| `vim.g.vsneo_cursor_animation_length` | `0.13` | Seconds. `0` turns the trail off. (Neovide: 0.150) |
| `vim.g.vsneo_cursor_short_animation_length` | `0.04` | Moves of at most two columns on one line: typing, `h`/`l`. |
| `vim.g.vsneo_cursor_trail_size` | `0.8` | 0..1, how much the trailing corners lag. `0` is a plain slide. (Neovide: 1.0) |
| `vim.g.vsneo_cursor_animate_in_insert_mode` | `true` | Also animate while typing. |

## Smooth scrolling

For scrolls nvim asks for (`<C-d>`, `<C-u>`, `<C-f>`, `<C-b>`, `zz`, `zt`,
`zb`, `<C-e>`, `<C-y>`, far jumps). Mouse-wheel scrolling stays Visual Studio's.

| Option | Default | |
|---|---|---|
| `vim.g.vsneo_scroll_animation_length` | `0.3` | Seconds. `0` = instant. |
| `vim.g.vsneo_scroll_animation_far_lines` | `1` | Past one screen, only the last N lines animate, so `G` in a long file is not a slow ride. |

## Jump beacon

A beacon.nvim-style bar that flashes at the cursor after a big jump and when a
document gains focus.

| Option | Default | |
|---|---|---|
| `vim.g.vsneo_beacon` | `true` | On/off. |
| `vim.g.vsneo_beacon_min_jump` | `10` | Lines a jump must cover to flash. |
| `vim.g.vsneo_beacon_width` | `40` | Columns. |
| `vim.g.vsneo_beacon_duration` | `0.4` | Seconds. |

## Cursor effects

Off by default, as in Neovide. One name or a list; effects combine.

```lua
vim.g.vsneo_cursor_vfx_mode = { 'matrix', 'sparks', 'flicker' }
```

| Name | Fires on | Looks like |
|---|---|---|
| `railgun` | every move | a curling helix of particles along the path |
| `torpedo` | every move | exhaust puffs thrown back behind the cursor |
| `pixiedust` | every move | sparkles shaken off, falling |
| `sonicboom` | every move | a ring expanding from the destination |
| `ripple` | every move | the cursor's outline rippling outward |
| `wireframe` | every move | a dashed box stretching wide |
| `glitch` | jumps, mode change, focus | red/cyan split copies and tear bars |
| `matrix` | jumps | katakana and 0/1 rain falling from the path |
| `circuit` | jumps | a right-angle neon trace drawn from old to new position |
| `scanline` | jumps across lines, focus | a CRT line sweeping the editor |
| `sparks` | typing | sparks off each typed character |
| `flicker` | mode change, focus | the cursor stutters like a failing neon tube |

"Every move" includes typing and single `h`/`j`/`k`/`l` steps. "Jumps" skips
those: `G`, `n`, `}`, `w`, a click. Previews are in
[`docs/cursor-vfx/`](cursor-vfx/). Unknown names are ignored. Writing your own
effect: [`docs/cursor-effects.md`](cursor-effects.md).

Tuning (Neovide's names and scales):

| Option | Default | |
|---|---|---|
| `vim.g.vsneo_cursor_vfx_opacity` | `200.0` | 0..255. |
| `vim.g.vsneo_cursor_vfx_particle_lifetime` | `0.5` | Seconds. `matrix` rain lives twice this. |
| `vim.g.vsneo_cursor_vfx_particle_highlight_lifetime` | `0.2` | Seconds, for the one-shot effects (rings, flashes, sweeps). |
| `vim.g.vsneo_cursor_vfx_particle_density` | `0.7` | Particles per line of travel, scaled. |
| `vim.g.vsneo_cursor_vfx_particle_speed` | `10.0` | |
| `vim.g.vsneo_cursor_vfx_particle_phase` | `1.5` | `railgun` only: how fast the helix turns along the path. |
| `vim.g.vsneo_cursor_vfx_particle_curl` | `1.0` | `railgun`, `torpedo`: how much particles keep turning. |

## Custom cursor

Off until `vsneo_cursor_style`, `vsneo_cursor_blinking` or `vsneo_cursor_color`
is set. Then VSNeo hides Visual Studio's caret and draws its own. Unset them
(and `:source`) to get Visual Studio's caret back.

| Option | Default | |
|---|---|---|
| `vim.g.vsneo_cursor_style` | see below | A string sets normal mode; a table sets any of `normal`, `insert`, `replace`, `visual`, `operator`, `cmdline`. |
| `vim.g.vsneo_cursor_blinking` | `'blink'` | VS Code's `cursorBlinking`: `'blink'`, `'smooth'`, `'phase'`, `'expand'` (shrinks to its centre), `'solid'`. |
| `vim.g.vsneo_cursor_color` | theme caret | `'#rrggbb'` or a highlight group name (its background, else its foreground). One value for every mode, or a table per mode like the style. A group name follows `:colorscheme`. |
| `vim.g.vsneo_cursor_glow` | off | `true` (12 px) or a blur radius, 0..60 px: a neon halo in the cursor's color. |

Styles: `'block'`, `'block-outline'`, `'line'`, `'line-thin'`, `'underline'`,
`'underline-thin'`. Defaults per mode: normal `block`, insert `line`, replace
`underline`, visual `block`, operator-pending `underline`, cmdline follows
normal. In visual mode the selection's cursor block is the cursor.

The trail, effects, beacon and mode line all use the cursor's current color,
and the command-line popup takes the `cmdline` color (normal's, unless a table
names `cmdline`) for its border, label, prompt and cursor. Without a color the
popup uses its own palette: blue commands, amber search, cyan Lua, purple
substitute.
Color palettes (Night City, Neon Tokyo, Netrunner, Blade Runner, Vaporwave)
are in the sample rc.

## Mode line

A modes.nvim-style wash of the current mode's color on the cursor line.

| Option | Default | |
|---|---|---|
| `vim.g.vsneo_mode_line` | `false` | On/off. Colors come from `vsneo_cursor_color` per mode; uncolored modes use teal (insert), red (replace), purple (visual), amber (operator-pending), and normal stays plain. |
| `vim.g.vsneo_mode_line_opacity` | `0.12` | 0..1. |

While it is on, the cursor line's number in the line-number margin also takes
the mode color.

## Line numbers

VSNeo's margin follows Neovim's own options, live:

| Option | |
|---|---|
| `vim.o.relativenumber` | Relative numbers in the margin. |
| `vim.o.number` | With `relativenumber`: the cursor line shows its absolute number, bold and left-aligned. Without it the cursor line shows `0`, as in Vim. |

Use `:set` / `vim.o`. Per-buffer `:setlocal` values do not stick: there is
one margin setting, re-applied on every buffer switch.

Visual Studio can override it: **Tools > Options > VSNeo > Editor > Relative
line numbers**: `FollowNeovim` (default), `AlwaysOn`, `AlwaysOff`.

## Search and highlight colors

nvim draws nothing here, but its highlight groups color what VSNeo draws, and
follow `:colorscheme`:

| Group | Colors |
|---|---|
| `Search` | all matches of the last search, while `hlsearch` is on |
| `CurSearch` (else `IncSearch`) | the match under the cursor |
| `IncSearch` | the match while typing `/` |

```vim
hi Search guibg=#3a3a00
```

`:nohlsearch` clears the highlights; `:set nohlsearch` turns them off.

| Option | Default | |
|---|---|---|
| `vim.g.vsneo_search_count` | `true` | A `[current/total]` chip at the end of the line while the cursor is on a match (nvim-hlslens style). Hidden in insert and replace. `false` turns it off. |

## Undo flash

| Option | Default | |
|---|---|---|
| `vim.g.vsneo_undo_flash` | `true` | `u` and `Ctrl+R` briefly highlight the text they changed. `false` turns it off. |

## Rendering

| Option | Default | |
|---|---|---|
| `vim.g.vsneo_reduce_effects` | detect | Unset: under software rendering (Remote Desktop, a GPU-less VM, Visual Studio's hardware acceleration off) the costly parts - effects, glow, smooth scrolling, fading blinks - stand down automatically. `true`: always reduce. `false`: never. |

Everything also respects Windows' **Show animations in Windows** setting: off
means no motion at all.

## Commands

| Command | |
|---|---|
| `:Vsc <command> [args]` | Run any Visual Studio command by its name in Tools > Options > Keyboard (`:vsc` works too). |
| `:VSNeoDnd [on\|off]` | Do not disturb; see above. |
| `:VSNeoPreset [name\|none]` | Switch the preset live; no argument lists them. See Presets. |
| `:e <file>` | Opens the file in Visual Studio (`:Edit`). `:e .` opens Solution Explorer. |
| `:b <name>` | Switches to an open document (`:Buffer`). |
| `:bn` / `:bp` | Next / previous tab (`:Bnext` / `:Bprevious`). |
| `:q`, `:wq`, `:x`, `:xit` | Close the document. `:qa` exits Visual Studio. |
| `:sp` / `:vsp` | Split / new vertical tab group. |

From Lua, in mappings:

| Function | |
|---|---|
| `vsneo.cmd(name, args)` | Run a Visual Studio command. |
| `vsneo.goto_cmd(name, args)` | The same, recording a jump first so `''` comes back. |
| `vsneo.multi_edit()` | Arm a multi-edit: the next change (`cw`, `ciw`, ...) is replayed at every match of the last search. |

```lua
vim.keymap.set('n', '<leader>b', function() vsneo.cmd('Build.BuildSolution') end, { desc = 'Build' })
```

## Default mappings

Rebind any of these in your rc.

| Keys | Runs |
|---|---|
| `gd` / `gD` / `gi` / `gr` | Go to definition / declaration / implementation, find all references |
| `[d` / `]d` | Previous / next error |
| `K` | Quick info |
| `<leader>rn` | Rename |
| `<leader>ca` | Quick actions |
| `<leader>f` | Format document |
| `<C-o>` / `<C-i>` (and `<Tab>`) | Visual Studio's navigate backward / forward |
| `zf` | Create a fold (a real Visual Studio outlining region) |
| `u` / `<C-r>` | Visual Studio's undo / redo (nvim's undo tree is not used) |
| `<C-w>` family, `:split`, `:vsplit` | Visual Studio's tab groups and splits |
| `<C-6>` | Alternate document, repeated presses walk further back |
| `gb` | Labeled jump to any open tab |
| `"` | Register peek popup |
| `s` | Jump to any visible match: type characters, then the label (flash-style) |
| `f` / `F` / `t` / `T` | As in Vim; with several matches on the line, labels pick one |
| `ZZ` / `ZQ` | Close the document |

## Options VSNeo sets for you

These are forced after your rc, because the editor sync depends on them;
setting them yourself has no effect: `wrap=false`, `scrolloff=0`,
`sidescrolloff=0`, `laststatus=0`, `swapfile=false`, `foldmethod=manual`,
`foldlevel=99`, `inccommand=''`. `clipboard=unnamedplus` is set when a
clipboard is available, so yanks and Visual Studio's clipboard are one.

Syntax highlighting and Treesitter are off by default, since Visual Studio
draws the text and nvim's highlighting is never seen. Unlike the list above,
this one can be undone: set `vim.g.vsneo_syntax = 1` and run `syntax on` in
your rc if an indent script or plugin needs syntax information.

## Environment variables

| Variable | |
|---|---|
| `VSNEO_NVIM_PATH` | Full path to `nvim.exe` when it is not on `PATH`. |
| `VSNEO_TRACE_KEYS` | `1` logs every key decision to `%TEMP%\vsneo.log` (diagnostics). |

## Presets

A preset is a whole look under one name: cursor colors and shape, blinking,
glow, trail, effects and mode line.

| Option | Default | |
|---|---|---|
| `vim.g.vsneo_preset` | none | `'blade_runner'`, `'matrix'` or `'cyberpunk2077'`. Case, spaces and dashes don't matter. |

`:VSNeoPreset <name>` switches live for the session; Tab completes the names.
`:VSNeoPreset none` goes back to your rc's look, and `:VSNeoPreset` alone
lists them with the active one marked. To keep one across restarts, set
`vim.g.vsneo_preset` in your rc.

| Preset | Look | Effects |
|---|---|---|
| `blade_runner` | orange / teal / magenta, expand blink | scanline, torpedo, flicker, sparks |
| `matrix` | phosphor green, red-pill replace, blue-pill visual, phase blink | matrix, sparks, flicker |
| `cyberpunk2077` | Night City yellow / netrunner cyan / Arasaka red, hard blink | glitch, circuit, scanline, sparks |

**Who wins depends on how the preset was picked:**
- **`:VSNeoPreset <name>`** wins over your rc for the session, so trying a
  look needs no rc edit. The whole look is the preset's: colors, cursor
  shape, blinking, glow, trail, effects and mode line, and a look setting the
  preset leaves out is the default rather than your rc's value. Settings
  outside the look (beacon size, smooth-scroll lengths, ...) stay yours.
  `:VSNeoPreset none`, or `:source` of your rc, brings the rc's look back.
- **`vim.g.vsneo_preset` in the rc** is a base: it fills in only what your rc
  doesn't set, so the rc can tweak a preset (`vsneo_cursor_glow = false` over
  Blade Runner, say).

Either way a preset is never written into your `vim.g` variables, so
switching leaves nothing behind. Do not disturb still turns everything off
over a preset.

The same values are in [`examples/vsneorc.lua`](../examples/vsneorc.lua) as
commented blocks, to copy and adjust.
