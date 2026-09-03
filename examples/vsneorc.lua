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

-- build / debug
vsc('<leader>bb', 'Build.BuildSolution', 'Build solution')
vsc('<leader>dd', 'Debug.Start', 'Start debugging')
vsc('<leader>ds', 'Debug.StopDebugging', 'Stop debugging')

-- git
vsc('<leader>gg', 'View.GitChanges', 'Git changes')

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
