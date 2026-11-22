# Companion modules

`VSNeo_Extension/Lua/vsneo.lua` is **generated**. It is the concatenation of
these files in filename order, and it is what ships in the VSIX and what
`tests/helper.lua` loads.

Edit a module, then run from the repository root:

```cmd
pwsh VSNeo_Extension/Lua/build-vsneo.ps1
```

`-Check` verifies the generated file is current without writing it; the VSIX
build and CI both run that, and fail with the command to fix it.

**Never edit `vsneo.lua` directly.** A change there is overwritten by the next
build and caught by `-Check`.

The modules are one Lua chunk, not `require()`d files: top-level locals are
shared exactly as they were in the single file, and filename order is the
execution order. The numeric prefixes are zero-padded to three digits so a
plain filename sort is the execution order.

| Module | Owns |
|---|---|
| `000-core.lua` | channel, filetype/syntax setup, the options the companion reasserts |
| `010-folds.lua` | region comparison, fold pushes, the `zf` range |
| `020-navigation.lua` | jump records, navigation and command mappings, window/split/focus |
| `030-windows.lua` | `:Vsc`/abbreviations, undo, overlays, tab jump, pickers |
| `040-search.lua` | search scanning, hlsearch/marks/scrollbar pushes |
| `050-commands.lua` | Visual Studio command catalog, `:Sethandler`, Solution Explorer |
| `060-syntax.lua` | Roslyn/Treesitter/region text objects and motions |
| `070-visual.lua` | options, presets, colors, effects and cursor configuration |
| `075-statusline.lua` | `laststatus=2`: the evaluated `'statusline'` pushed for each view's statusline margin |
| `080-keymaps.lua` | the keymap/imap snapshot sent to the which-key popup |
| `090-dot.lua` | insert bracket and `.` replay capture |
| `100-macros.lua` | recording rewrite and multi-edit replay |
| `110-multi.lua` | `vsneo.multi_edit()` |
