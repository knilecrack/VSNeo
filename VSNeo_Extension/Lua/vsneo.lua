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

-- Yank and put go through the system clipboard, so Vim's registers and Visual
-- Studio's Ctrl+C / Ctrl+V are the same thing. Without this, y in visual mode
-- fills a register nothing in Visual Studio can reach, and pasting into another
-- application quietly gets whatever was there before.
--
-- Safe to set unconditionally here: has('clipboard_working') is 1 on this
-- platform. Were there no provider, every yank would raise an error message
-- instead - and nothing renders those.
if vim.fn.has('clipboard_working') == 1 then
  vim.o.clipboard = 'unnamedplus'
end

-- 'inccommand' previews :s/ by really editing the buffer and reverting it.
-- Those previews arrive as buffer events carrying a null changedtick, and
-- nothing here renders them - so at best they are RPC on every keystroke of a
-- substitution, and at worst one missed guard writes a preview into the real
-- file, where it stays: nvim's revert is not a buffer change and produces no
-- event to undo it with. BufferMirror drops null-tick events regardless; this
-- stops them being sent at all.
vim.o.inccommand = ''

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

------------------------------------------------------------------
-- Window management
--
-- Visual Studio is the window manager here, not nvim. nvim is headless
-- and has exactly one window, so Vim's :split, :vsplit and the Ctrl-w
-- family would otherwise operate on a window nobody can see. We map
-- them to the VS commands that produce the same layout.
--
-- Note: VS has no directional "go left / go right" between splits, only
-- next / previous, so the hjkl mappings are approximations.
------------------------------------------------------------------

-- :split and :vsplit would otherwise try to split nvim's own window.
vim.cmd([[cnoreabbrev <expr> sp    getcmdtype() == ':' ? 'lua vsneo.cmd("Window.Split")'                : 'sp']])
vim.cmd([[cnoreabbrev <expr> split getcmdtype() == ':' ? 'lua vsneo.cmd("Window.Split")'                : 'split']])
vim.cmd([[cnoreabbrev <expr> vsp   getcmdtype() == ':' ? 'lua vsneo.cmd("Window.NewVerticalTabGroup")' : 'vsp']])
vim.cmd([[cnoreabbrev <expr> vsplit getcmdtype() == ':' ? 'lua vsneo.cmd("Window.NewVerticalTabGroup")' : 'vsplit']])

-- The quit family must never reach nvim itself: this headless instance has one
-- window, so :q exits the process and the whole session dies with it - nothing
-- restarts it, and the log just shows the pipe closing. Visual Studio owns
-- windows and lifetime; route there instead.
vim.cmd([[cnoreabbrev <expr> q     getcmdtype() == ':' ? 'lua vsneo.cmd("Window.CloseDocumentWindow")' : 'q']])
vim.cmd([[cnoreabbrev <expr> quit  getcmdtype() == ':' ? 'lua vsneo.cmd("Window.CloseDocumentWindow")' : 'quit']])
vim.cmd([[cnoreabbrev <expr> wq    getcmdtype() == ':' ? 'lua vsneo.cmd("Window.CloseDocumentWindow")' : 'wq']])
vim.cmd([[cnoreabbrev <expr> x     getcmdtype() == ':' ? 'lua vsneo.cmd("Window.CloseDocumentWindow")' : 'x']])
vim.cmd([[cnoreabbrev <expr> xit   getcmdtype() == ':' ? 'lua vsneo.cmd("Window.CloseDocumentWindow")' : 'xit']])
vim.cmd([[cnoreabbrev <expr> qa    getcmdtype() == ':' ? 'lua vsneo.cmd("File.Exit")' : 'qa']])
vim.cmd([[cnoreabbrev <expr> qall  getcmdtype() == ':' ? 'lua vsneo.cmd("File.Exit")' : 'qall']])
vim.keymap.set('n', 'ZZ', function() _G.vsneo.cmd('Window.CloseDocumentWindow') end,
  { silent = true, desc = 'VSNeo: close document' })
vim.keymap.set('n', 'ZQ', function() _G.vsneo.cmd('Window.CloseDocumentWindow') end,
  { silent = true, desc = 'VSNeo: close document' })

-- :w reaches the BufWriteCmd above, which clears 'modified' and writes NOTHING:
-- Visual Studio owns the file, so saving has to go through it.
vim.cmd([[cnoreabbrev <expr> w     getcmdtype() == ':' ? 'lua vsneo.cmd("File.SaveSelectedItems")' : 'w']])

local function win(lhs, command)
  vim.keymap.set('n', lhs, function() _G.vsneo.cmd(command) end,
    { silent = true, desc = 'VSNeo: ' .. command })
end

win('<C-w>s', 'Window.Split')
win('<C-w>v', 'Window.NewVerticalTabGroup')
win('<C-w>h', 'Window.PreviousSplitPane')
win('<C-w>j', 'Window.NextSplitPane')
win('<C-w>k', 'Window.PreviousSplitPane')
win('<C-w>l', 'Window.NextSplitPane')
win('<C-w>w', 'Window.NextSplitPane')
win('<C-w>q', 'Window.CloseDocumentWindow')
win('<C-w>c', 'Window.CloseDocumentWindow')

-- Vim's insert-mode Ctrl-w deletes the word before the cursor. Visual Studio's
-- own Ctrl+W is unbound by KeyBindingCleaner so the prefix works in normal
-- mode; this keeps the insert-mode behaviour.
vim.keymap.set('i', '<C-w>', '<C-o>db', { silent = true, desc = 'VSNeo: delete word backward' })

------------------------------------------------------------------
-- Search highlights (hlsearch)
--
-- nvim owns the pattern and the regex engine; Visual Studio owns the
-- pixels. We ask nvim for every match of getreg('/') and send the
-- positions over RPC so the extension can draw them. Keeping the regex
-- here means Vim's own syntax (\v, \c, \<, etc.) works unchanged.
------------------------------------------------------------------

local last_search_pattern = nil

local function send_search_matches(force)
  -- Pattern and hlsearch state both live in nvim. Nothing to send means
  -- "clear the highlights", which is exactly what :nohlsearch should do.
  if vim.v.hlsearch == 0 then
    last_search_pattern = nil
    vim.rpcnotify(chan, 'vsneo_search_matches', {})
    return
  end

  local pattern = vim.fn.getreg('/')
  if pattern == '' then
    last_search_pattern = nil
    vim.rpcnotify(chan, 'vsneo_search_matches', {})
    return
  end

  -- Same pattern, no edit: nothing changed. This keeps CursorMoved cheap.
  if not force and pattern == last_search_pattern then
    return
  end
  last_search_pattern = pattern

  -- A pattern that does not compile (for example while it is still being
  -- typed) has no matches to show, and matchstrpos would throw on it.
  if not pcall(vim.regex, pattern) then
    return
  end

  local buf = vim.api.nvim_get_current_buf()
  local lines = vim.api.nvim_buf_get_lines(buf, 0, -1, false)
  local matches = {}

  -- Hard cap: a one-character pattern in a big file is one match per
  -- character, and the whole list travels in a single msgpack frame.
  local max_matches = 5000

  for i, line in ipairs(lines) do
    local offset = 0
    while offset <= #line do
      -- matchstrpos, not vim.regex:match_str. match_str's extra start
      -- argument is silently ignored by the C binding, so it returns the
      -- first match in the line on every iteration, offset never advances,
      -- and the loop wedges nvim's single-threaded main loop - the
      -- "extension dies after a / search" hang. matchstrpos takes a real
      -- byte offset and honours anchors against the whole line.
      local m = vim.fn.matchstrpos(line, pattern, offset)
      local s, e = m[2], m[3]
      if s < 0 then break end
      -- 0-based line, 0-based byte columns: ColumnMapper on the C# side
      -- expects exactly this.
      table.insert(matches, { i - 1, s, e })
      -- An empty match (for example ^) must advance or the loop never ends.
      offset = e == s and (e + 1) or e
      if #matches >= max_matches then break end
    end
    if #matches >= max_matches then break end
  end

  vim.rpcnotify(chan, 'vsneo_search_matches', matches)
end

-- After a / or ? search is entered.
vim.api.nvim_create_autocmd('CmdlineLeave', {
  group = group,
  pattern = { '/', '?' },
  callback = function()
    vim.defer_fn(function() send_search_matches(true) end, 50)
  end,
})

-- After edits and buffer switches the matches may have moved.
vim.api.nvim_create_autocmd({ 'TextChanged', 'TextChangedI', 'BufEnter' }, {
  group = group,
  callback = function()
    vim.defer_fn(function() send_search_matches(true) end, 100)
  end,
})

-- * and # set the pattern without leaving a command line. CursorMoved is the
-- only signal they produce, and the pattern check inside keeps this cheap.
vim.api.nvim_create_autocmd('CursorMoved', {
  group = group,
  callback = function()
    vim.defer_fn(function() send_search_matches(false) end, 50)
  end,
})
