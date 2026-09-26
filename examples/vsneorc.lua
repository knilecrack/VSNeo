-- VSNeo example Lua config — copy to ~/.vsneorc.lua
--
-- LazyVim-flavored: space leader, mappings with descriptions (the which-key
-- popup reads desc first; vimscript's :nnoremap cannot carry one, which is
-- why this file exists). The companion sources ~/.vsneorc (vimscript) first,
-- then this file, then pushes the mapping table for the popup.
--
-- Companion already provides: gd/gD/gi/gr, [d/]d, K, <leader>rn, <leader>ca,
-- <leader>f, gb (tab jumper), s (jump labels), <C-w>h/j/k/l (split nav).
-- This file adds the LazyVim-style leader groups on top.

vim.g.mapleader = ' '

-- Neovide-style cursor trail (Neovide's names, vsneo_ prefix). Defaults shown;
-- a length of 0 turns it off. Also off while Windows animations are disabled.
-- vim.g.vsneo_cursor_animation_length = 0.13
-- vim.g.vsneo_cursor_short_animation_length = 0.04  -- typing, h/l: <= 2 columns on one line
-- vim.g.vsneo_cursor_trail_size = 0.8
-- vim.g.vsneo_cursor_animate_in_insert_mode = true
-- Neovide's particle effects, off by default. One mode or a list:
-- 'railgun', 'torpedo', 'pixiedust', 'sonicboom', 'ripple', 'wireframe'.
-- vim.g.vsneo_cursor_vfx_mode = 'railgun'
-- vim.g.vsneo_cursor_vfx_opacity = 200.0                 -- 0..255
-- vim.g.vsneo_cursor_vfx_particle_lifetime = 0.5
-- vim.g.vsneo_cursor_vfx_particle_highlight_lifetime = 0.2
-- vim.g.vsneo_cursor_vfx_particle_density = 0.7
-- vim.g.vsneo_cursor_vfx_particle_speed = 10.0
-- vim.g.vsneo_cursor_vfx_particle_phase = 1.5             -- railgun
-- vim.g.vsneo_cursor_vfx_particle_curl = 1.0              -- railgun, torpedo

-- VSNeo's own cursor instead of Visual Studio's caret (off until either is set).
-- Styles: 'block', 'block-outline', 'line', 'line-thin', 'underline', 'underline-thin'.
-- A string sets normal mode; a table sets any of normal/insert/replace/visual/operator/cmdline.
-- Blinking (VS Code's): 'blink', 'smooth', 'phase', 'expand' (shrinks to its centre), 'solid'.
-- vim.g.vsneo_cursor_style = { normal = 'block-outline', insert = 'line', replace = 'underline' }
-- vim.g.vsneo_cursor_blinking = 'expand'

-- Cursor color: '#rrggbb' or a highlight group name, one for every mode or a
-- table per mode. The trail and particles follow it. vsneo_cursor_glow adds
-- a neon halo (true = 12 px, or a radius in px). Cyberpunk presets
-- (preview: docs/cursor-vfx/cyberpunk-palettes.png) - pick one:
--
-- Night City (Cyberpunk 2077 yellow / cyan / red)
-- vim.g.vsneo_cursor_color = { normal = '#FCEE0A', insert = '#00F0FF', replace = '#FF003C', operator = '#FF003C' }
-- Neon Tokyo (hot pink / cyan)
-- vim.g.vsneo_cursor_color = { normal = '#FF2A6D', insert = '#05D9E8', replace = '#F9F002', operator = '#D1F7FF' }
-- Netrunner (acid green)
-- vim.g.vsneo_cursor_color = { normal = '#39FF14', insert = '#00FF9F', replace = '#FF073A', operator = '#F5F500' }
-- Blade Runner (orange / teal / magenta)
-- vim.g.vsneo_cursor_color = { normal = '#FF6C11', insert = '#2DE2E6', replace = '#F706CF', operator = '#FFD319' }
-- Vaporwave (purple / sky / pink)
-- vim.g.vsneo_cursor_color = { normal = '#B967FF', insert = '#01CDFE', replace = '#FF71CE', operator = '#FFFB96' }
--
-- vim.g.vsneo_cursor_glow = true

local function vsc(lhs, command, desc)
  vim.keymap.set('n', lhs, function() vsneo.cmd(command) end,
    { silent = true, desc = desc })
end

-- find
vsc('<leader>ff', 'Edit.GoToAll', 'Find file/symbol (Go To All)')
vsc('<leader>fg', 'Edit.FindinFiles', 'Grep (Find in Files)')

-- buffers / tabs
vsc('<leader>bn', 'Window.NextTab', 'Next tab')
vsc('<leader>bp', 'Window.PreviousTab', 'Previous tab')

-- code
vsc('<leader>cr', 'Refactor.Rename', 'Rename symbol')
vsc('<leader>cf', 'Edit.FormatDocument', 'Format document')
vsc('<leader>ce', 'View.ErrorList', 'Error list')

-- multi-edit: arm the matches of the last search (/ or *), change one with
-- cgn/ciw, and Esc replays the change at every other match
vim.keymap.set('n', '<leader>mm', function() vsneo.multi_edit() end,
  { silent = true, desc = 'Multi-edit all search matches' })

-- build / debug
vsc('<leader>bb', 'Build.BuildSolution', 'Build solution')
vsc('<leader>dd', 'Debug.Start', 'Start debugging')
vsc('<leader>ds', 'Debug.StopDebugging', 'Stop debugging')

-- git
vsc('<leader>gg', 'Team.Git.GoToGitChanges', 'Git changes')

-- windows
vsc('<leader>ex', 'View.SolutionExplorer', 'Solution Explorer')

------------------------------------------------------------------
-- Plugins (standard packages layout rooted at ~/.vsneo; verified working
-- headless against VSNeo's exact startup flags):
--
--   git clone https://github.com/echasnovski/mini.ai       ~/.vsneo/pack/lazy/start/mini.ai
--   git clone https://github.com/echasnovski/mini.surround ~/.vsneo/pack/lazy/start/mini.surround
--   git clone https://github.com/chrisgrieser/nvim-spider  ~/.vsneo/pack/lazy/start/nvim-spider
--
-- mini.nvim defines nothing until setup() (lazy.nvim normally makes that
-- call; here we are our own distro). The pcall keeps this rc harmless when
-- a plugin is not installed.
------------------------------------------------------------------

pcall(function() require('mini.ai').setup() end)        -- argument textobject: cia, daa, ...
pcall(function() require('mini.surround').setup() end)  -- saiw) sd" sr"'

-- nvim-spider: camelCase/subword motions, the C# spelling of w/e/b.
-- Commented out by default because it remaps core motions; uncomment to try.
-- local ok, spider = pcall(require, 'spider')
-- if ok then
--   for _, key in ipairs({ 'w', 'e', 'b' }) do
--     vim.keymap.set({ 'n', 'o', 'x' }, key,
--       function() spider.motion(key) end,
--       { desc = 'spider ' .. key })
--   end
-- end
