-- VSNeo example config - copy to %LOCALAPPDATA%\vsneo\init.lua
--
-- This directory is an ordinary Neovim config (init.lua, lua/, plugin/,
-- after/), kept apart from your terminal Neovim: plugins install under
-- %LOCALAPPDATA%\vsneo-data. Once this file exists, ~/.vsneorc and
-- ~/.vsneorc.lua are no longer read; every vsneo_* option and mapping shown
-- in examples/vsneorc.lua works here unchanged.
--
-- VSNeo sources it after its own defaults, so vsneo.cmd() is available and
-- your mappings win.

vim.g.mapleader = ' '

vim.keymap.set('n', '<leader>b', function() vsneo.cmd('Build.BuildSolution') end,
  { desc = 'Build solution' })

------------------------------------------------------------------
-- Plugins, with lazy.nvim. Buffer and motion plugins are what fit: Visual
-- Studio draws the text, so anything with its own UI (statusline, file tree,
-- pickers, completion) has nothing to draw on, and LSP, formatters and
-- session plugins fight Visual Studio for the file. See docs/options.md.
--
-- VimEnter has already passed and mirrored buffers fire no BufRead events:
-- load plugins at startup, on 'VeryLazy', or on their keys.
------------------------------------------------------------------
local lazypath = vim.fn.stdpath('data') .. '/lazy/lazy.nvim'
if not vim.uv.fs_stat(lazypath) then
  vim.fn.system({ 'git', 'clone', '--filter=blob:none', '--branch=stable',
    'https://github.com/folke/lazy.nvim.git', lazypath })
end
vim.opt.rtp:prepend(lazypath)

require('lazy').setup({
  { 'echasnovski/mini.ai', event = 'VeryLazy', opts = {} },
  { 'echasnovski/mini.surround', event = 'VeryLazy', opts = {} },
  { 'chrisgrieser/nvim-spider', keys = {
    { 'w', function() require('spider').motion('w') end, mode = { 'n', 'o', 'x' } },
    { 'e', function() require('spider').motion('e') end, mode = { 'n', 'o', 'x' } },
    { 'b', function() require('spider').motion('b') end, mode = { 'n', 'o', 'x' } },
  } },
})

-- Sharing your terminal config instead (Config directory = %LOCALAPPDATA%\nvim):
-- load only the specs marked for VSNeo, the way LazyVim does for VS Code.
--
--   require('lazy').setup(specs, {
--     defaults = { cond = vim.g.vsneo and function(plugin) return plugin.vsneo end or nil },
--   })
--   -- and on each spec that should load here:  { 'echasnovski/mini.ai', vsneo = true }
