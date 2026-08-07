-- VSNeo's companion, running inside the embedded nvim.
--
-- Loaded from disk rather than embedded in the C# assembly. It used to be a
-- verbatim string literal, where every double quote had to be doubled - and a
-- comment mentioning E325 silently terminated the string and broke the build.
-- As a real file it gets syntax highlighting, needs no escaping, and can be
-- read on its own.
--
-- It still ships inside the VSIX beside the DLL, so it cannot drift out of step
-- with the rpcnotify contract the extension expects. This is not the user's
-- init.lua; that is separate, opt-in, and loaded later.
--
-- Receives the RPC channel id as its only argument.

local chan = ...

vim.cmd('filetype plugin indent on')

-- Visual Studio decides what wraps. If nvim wrapped as well its screen
-- lines would stop matching VS's, and H, M, L and the <C-d> family are
-- all defined in screen lines - they would drift by however many lines
-- nvim thought had wrapped.
vim.o.wrap = false

-- Nothing renders nvim's own scroll padding, and a non-zero value here
-- makes nvim scroll the window when VS would not have, desynchronising
-- the topline the viewport synchroniser just set.
vim.o.scrolloff = 0
vim.o.sidescrolloff = 0

-- Nothing draws a status line either, and every row it occupies is a row
-- the text window does not have. Measured: with ext_cmdline on, a grid of
-- 30 gives a 29-line window by default and a 30-line window with this
-- off. Zero chrome means the viewport synchroniser can pass Visual
-- Studio's visible line count straight through, and <C-d> then scrolls by
-- what you can actually see.
vim.o.laststatus = 0

-- No swap files, and this one is not an optimisation. Naming a buffer
-- after a real path makes nvim treat it as a real file, so it looks for a
-- swap file, and on finding one it raises the modal E325 ATTENTION
-- prompt. Nothing renders that prompt, so nvim simply stops: mode()
-- reports 'r?', a confirm query, and every keystroke is swallowed
-- answering a question nobody can see. Visual Studio owns the file and
-- its recovery story; a second one here can only deadlock us.
vim.o.swapfile = false
vim.o.backup = false
vim.o.writebackup = false

-- Belt and braces: A suppresses the swap-file ATTENTION message even if
-- something contrives to create one.
vim.opt.shortmess:append('A')
vim.api.nvim_create_autocmd('BufWriteCmd', {
  pattern = '*',
  callback = function(ev)
    vim.bo[ev.buf].modified = false
  end,
})

local group = vim.api.nvim_create_augroup('VSNeo', { clear = true })

local function push()
  local ok, pos = pcall(vim.api.nvim_win_get_cursor, 0)
  if not ok then return end

  local m = vim.api.nvim_get_mode().mode
  local kind = m:sub(1, 1)

  -- The other end of a visual selection. Vim keeps it in the 'v' mark,
  -- and it is the half Visual Studio cannot infer: the cursor alone says
  -- where the selection ends but nothing about where it began, so without
  -- this the mode changes and nothing appears selected.
  local aline, acol = -1, -1
  if kind == 'v' or kind == 'V' or kind == '\22'
     or kind == 's' or kind == 'S' or kind == '\19' then
    local v = vim.fn.getpos('v')
    aline, acol = v[2] - 1, v[3] - 1   -- 1-based line, 1-based byte column
  end

  -- row is 1-based from nvim and 0-based everywhere in the extension;
  -- col is already a 0-based byte offset, which is what ColumnMapper wants.
  -- line('w0') is the first visible line: zz, zt, zb and <C-e> move only
  -- this and never the cursor, so without it they are invisible.
  vim.rpcnotify(chan, 'vsneo_state',
    m, pos[1] - 1, pos[2], vim.fn.line('w0') - 1, aline, acol)
end

vim.api.nvim_create_autocmd({ 'CursorMoved', 'CursorMovedI', 'BufEnter', 'WinScrolled' }, {
  group = group,
  callback = push,
})

-- ModeChanged matches against 'old:new', so it needs its own pattern.
vim.api.nvim_create_autocmd('ModeChanged', {
  group = group,
  pattern = '*:*',
  callback = push,
})

push()

------------------------------------------------------------------
-- Running Visual Studio commands from Vim mappings.
--
-- This is the point of keeping VS as the editor. Roslyn already knows
-- where a symbol is defined across projects and assemblies; Vim's own
-- gd is a same-file text search that would be strictly worse here. So
-- the familiar keys are wired to the real thing.
--
-- vsneo.cmd runs any command by the name shown in
-- Tools > Options > Keyboard, so anything Visual Studio can do is
-- reachable from a mapping:
--
--   vim.keymap.set('n', '<leader>b', function()
--     vsneo.cmd('Build.BuildSolution')
--   end)
------------------------------------------------------------------
_G.vsneo = {
  channel = chan,

  cmd = function(name, args)
    vim.rpcnotify(chan, 'vsneo_action', name, args or '')
  end,

  -- Same, but records the jump first. Visual Studio moves the caret
  -- itself, and nvim would see only a cursor move rather than a jump,
  -- leaving <C-o> with nowhere to go back to.
  goto_cmd = function(name, args)
    vim.cmd("normal! m'")
    vim.rpcnotify(chan, 'vsneo_action', name, args or '')
  end,
}

local function nav(lhs, command)
  vim.keymap.set('n', lhs, function() _G.vsneo.goto_cmd(command) end,
    { silent = true, desc = 'VSNeo: ' .. command })
end

local function act(lhs, command)
  vim.keymap.set('n', lhs, function() _G.vsneo.cmd(command) end,
    { silent = true, desc = 'VSNeo: ' .. command })
end

nav('gd', 'Edit.GoToDefinition')
nav('gD', 'Edit.GoToDeclaration')
nav('gi', 'Edit.GoToImplementation')
nav('gr', 'Edit.FindAllReferences')
nav('[d', 'View.PreviousError')
nav(']d', 'View.NextError')

act('K', 'Edit.QuickInfo')
act('<leader>rn', 'Refactor.Rename')
act('<leader>ca', 'View.QuickActionsForPosition')
act('<leader>f', 'Edit.FormatDocument')
