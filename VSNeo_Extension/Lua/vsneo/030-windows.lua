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
-- The <leader> defaults (rn, ca, f) are defined after the rc is sourced,
-- below: <leader> expands when a mapping is created, so defined here they
-- were always backslash, whatever mapleader the rc set.

-- Navigation history is Visual Studio's too. Its stack records F12, Find All
-- References, error-list jumps and Ctrl+- - none of which nvim's jumplist
-- ever sees - and walking it needs no cross-file follow machinery: Visual
-- Studio moves the caret (or the tab) and the usual sync pushes nvim along.
-- nvim's jumplist is still written by motions and goto_cmd's m', so '' and
-- g;/g, keep working natively. <C-i> and <Tab> are one key to nvim, so plain
-- Tab in normal mode walks forward too - Vim's own default does the same.
act('<C-o>', 'View.NavigateBackward')
act('<C-i>', 'View.NavigateForward')

-- Folding is Visual Studio outlining, mirrored into nvim as manual folds
-- (see the top of this file and vsneo.folds_set): region boundaries come from
-- Visual Studio and the closed state syncs both ways, so za, zo, zc, zd, zR,
-- zM and the zj/zk/[z/]z motions are all native here - every change is caught
-- by the detection in push() and applied to VS's outlining.
--
-- zf is the one mapping: it must NOT create an nvim-only fold (those are
-- transient - the next full sync recreates only VS-known regions), so the
-- range goes to Visual Studio, where UserFoldTagger turns it into a real
-- outlining region; the RegionsCollapsed event round-trips back and creates
-- the manual fold here. zd stays native and the detection sorts out the
-- semantics: a user fold's region is removed, a language fold only expands.
-- The :fold ex-command remains native and nvim-only (transient).
local function fold_create(s, e)
  vim.rpcnotify(chan, 'vsneo_fold_create', vim.api.nvim_buf_get_name(0), s, e)
end

vim.keymap.set('x', 'zf', function()
  local s = vim.fn.getpos('v')[2]
  local e = vim.fn.getpos('.')[2]
  if s > e then s, e = e, s end
  local esc = vim.api.nvim_replace_termcodes('<Esc>', true, false, true)
  vim.api.nvim_feedkeys(esc, 'nx', false)
  fold_create(s, e)
end, { silent = true, desc = 'VSNeo: create fold' })

-- zf{motion}: g@ drives the motion and calls back through 'operatorfunc',
-- which is where the '[ and '] marks hold the covered range. zf folds whole
-- lines, so only the lines matter, whatever the motion's columns were.
function _G.vsneo_zf_op()
  fold_create(vim.fn.getpos("'[")[2], vim.fn.getpos("']")[2])
end
vim.keymap.set('n', 'zf', function()
  vim.go.operatorfunc = 'v:lua.vsneo_zf_op'
  return 'g@'
end, { expr = true, silent = true, desc = 'VSNeo: create fold' })

------------------------------------------------------------------
-- Window management
--
-- Visual Studio is the window manager here, not nvim. nvim is headless
-- and has exactly one window, so Vim's :split, :vsplit and the Ctrl-w
-- family would otherwise operate on a window nobody can see. We map
-- them to the VS commands that produce the same layout.
--
-- Ctrl-W h/j/k/l (and the arrow variants) are directional for real: the
-- extension enumerates the on-screen document frames, compares their screen
-- rectangles, and focuses the group adjacent to the active one. Ctrl-W w stays
-- a cycle - sometimes "just the other one" is all that is wanted.
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
-- Each abbreviation names a user command, never ':lua ...': :lua swallows the
-- rest of the line, so :q!, :w foo or :w | e x were Lua syntax errors (E5107)
-- and nothing ran. As commands, the bang, arguments and | parse as Ex syntax.
-- The guard expands only a word that is the whole command so far, alone or
-- after a | (:w | q), so :%s/q/x/ is left alone (and :s/a\|q/, whose bar is
-- escaped). A ranged :'<,'>w never expands - an abbreviation needs a space or
-- line start before it - and reaches the BufWriteCmd above, a harmless no-op.
local function vs_abbrev(lhs, command)
  vim.keymap.set('ca', lhs, function()
    if vim.fn.getcmdtype() ~= ':' then return lhs end
    local before = vim.fn.getcmdline():sub(1, vim.fn.getcmdpos() - 1)
    if before:match('^%s*' .. lhs .. '$') or before:match('^[^|]*[^|\\]|%s*' .. lhs .. '$') then
      return command
    end
    return lhs
  end, { expr = true, desc = 'VSNeo: :' .. lhs .. ' -> :' .. command })
end
-- Visual Studio has no close-without-saving command: the bang is accepted so
-- :q! and :wq! run, and Visual Studio's own save prompt decides.
vim.api.nvim_create_user_command('Quit', function() _G.vsneo.cmd('Window.CloseDocumentWindow') end,
  { bang = true, bar = true, nargs = '*', range = true, desc = 'VSNeo: close the document' })
vim.api.nvim_create_user_command('Qall', function() _G.vsneo.cmd('File.Exit') end,
  { bang = true, bar = true, nargs = '*', range = true, desc = 'VSNeo: exit Visual Studio' })
-- :wq and :x save first, as in Vim; Visual Studio's prompt then has nothing to ask.
vim.api.nvim_create_user_command('Xit', function()
  _G.vsneo.cmd('File.SaveSelectedItems')
  _G.vsneo.cmd('Window.CloseDocumentWindow')
end, { bang = true, bar = true, nargs = '*', range = true, desc = 'VSNeo: save and close the document' })
for _, lhs in ipairs({ 'q', 'quit' }) do vs_abbrev(lhs, 'Quit') end
for _, lhs in ipairs({ 'wq', 'x', 'xit' }) do vs_abbrev(lhs, 'Xit') end
for _, lhs in ipairs({ 'qa', 'qall' }) do vs_abbrev(lhs, 'Qall') end
vim.keymap.set('n', 'ZZ', function() _G.vsneo.cmd('Window.CloseDocumentWindow') end,
  { silent = true, desc = 'VSNeo: close document' })
vim.keymap.set('n', 'ZQ', function() _G.vsneo.cmd('Window.CloseDocumentWindow') end,
  { silent = true, desc = 'VSNeo: close document' })

-- :w reaches the BufWriteCmd above, which clears 'modified' and writes NOTHING:
-- Visual Studio owns the file, so saving has to go through it.
-- :w foo has no Visual Studio equivalent (Save As is a dialog): it saves the
-- document under its own name and says the name was ignored.
vim.api.nvim_create_user_command('Write', function(opts)
  _G.vsneo.cmd('File.SaveSelectedItems')
  if opts.args ~= '' then
    vim.api.nvim_echo({ { 'VSNeo: saved the document itself; "' .. opts.args
      .. '" ignored (no write-to-name in Visual Studio)', 'WarningMsg' } }, true, {})
  end
end, { bang = true, bar = true, nargs = '*', range = true, complete = 'file', desc = 'VSNeo: save the document' })
vs_abbrev('w', 'Write')
vs_abbrev('write', 'Write')

-- nvim's undo tree must never run: its root is the mirror's initial empty
-- buffer, and the mirror applies every step it walks into Visual Studio (how
-- a stray u once emptied whole files). u and <C-r> are intercepted
-- Visual Studio-side; :undo, :redo, :earlier and :later come here and take
-- one Visual Studio step. A count or time cannot be honored and says so.
local function vs_undo(command)
  return function(opts)
    _G.vsneo.cmd(command)
    if opts.args ~= '' then
      vim.api.nvim_echo({ { 'VSNeo: one ' .. command .. ' step; "' .. opts.args
        .. '" ignored (Visual Studio owns undo)', 'WarningMsg' } }, true, {})
    end
  end
end
vim.api.nvim_create_user_command('Undo', vs_undo('Edit.Undo'),
  { bang = true, bar = true, nargs = '*', desc = 'VSNeo: Visual Studio undo' })
vim.api.nvim_create_user_command('Redo', vs_undo('Edit.Redo'),
  { bang = true, bar = true, nargs = '*', desc = 'VSNeo: Visual Studio redo' })
for _, lhs in ipairs({ 'u', 'un', 'undo', 'earlier' }) do vs_abbrev(lhs, 'Undo') end
for _, lhs in ipairs({ 'red', 'redo', 'later' }) do vs_abbrev(lhs, 'Redo') end

local function win(lhs, command)
  vim.keymap.set('n', lhs, function() _G.vsneo.cmd(command) end,
    { silent = true, desc = 'VSNeo: ' .. command })
end

win('<C-w>s', 'Window.Split')
win('<C-w>v', 'Window.NewVerticalTabGroup')
-- Focus movement goes through a notification rather than vsneo.cmd: no VS
-- command can answer "which group is to my left", only the extension's frame
-- geometry can. See SplitNavigator.cs.
local function focus(lhs, dir)
  vim.keymap.set('n', lhs, function() vim.rpcnotify(chan, 'vsneo_focus', dir) end,
    { silent = true, desc = 'VSNeo: focus ' .. dir .. ' split' })
end

focus('<C-w>h', 'left')
focus('<C-w>j', 'down')
focus('<C-w>k', 'up')
focus('<C-w>l', 'right')
focus('<C-w><Left>', 'left')
focus('<C-w><Down>', 'down')
focus('<C-w><Up>', 'up')
focus('<C-w><Right>', 'right')
win('<C-w>w', 'Window.NextSplitPane')
win('<C-w>q', 'Window.CloseDocumentWindow')
win('<C-w>c', 'Window.CloseDocumentWindow')

-- Alternate file / MRU walk. First press toggles to the previously used
-- document; pressing again within two seconds walks further back through the
-- most-recently-used documents (Ctrl+Tab semantics without the popup). The
-- history lives in Visual Studio: nvim's own <C-^> would only switch its
-- hidden buffer, and the editor would not follow.
local function mru() vim.rpcnotify(chan, 'vsneo_mru') end
vim.keymap.set('n', '<C-6>', mru, { silent = true, desc = 'VSNeo: previous document (MRU walk)' })
vim.keymap.set('n', '<C-^>', mru, { silent = true, desc = 'VSNeo: previous document (MRU walk)' })

-- Labeled jump to any open tab: Visual Studio rewrites the tab captions with
-- letters and reads the pick back through the overlay conversation below.
vim.keymap.set('n', 'gb', function() vim.rpcnotify(chan, 'vsneo_tabs') end,
  { silent = true, desc = 'VSNeo: jump to a tab by label' })

-- Vim's insert-mode Ctrl-w deletes the word before the cursor. The key
-- processor claims the chord and deletes Visual Studio-side, asking
-- vsneo.word_back_boundary where i_CTRL-W would stop; nvim's own cursor
-- cannot be pushed to one-past-the-end reliably enough to delete from.
-- Visual Studio's own Ctrl+W is unbound by KeyBindingCleaner so the prefix
-- also works in normal mode.

------------------------------------------------------------------
-- Overlay interactions and jump labels
--
-- nvim is the brain but owns no pixels: the editor surface belongs to
-- Visual Studio. An overlay interaction is a conversation - Lua says when
-- it starts (the command filter then routes Escape/Enter/Backspace to
-- nvim, exactly as in CmdLine mode) and pushes labels to draw, and Visual
-- Studio renders them. flash.nvim itself cannot be that driver: its
-- labels are extmark virtual text on nvim's grid, and nvim 0.12 reports
-- no extmarks to external UIs. vsneo.jump is the same habit rebuilt on
-- this channel.
------------------------------------------------------------------

local function overlay_active(on)
  vim.rpcnotify(chan, 'vsneo_overlay_active', on and 1 or 0)
end

local function overlay_labels(items)
  vim.rpcnotify(chan, 'vsneo_overlay_labels', items)
end

--- Reads the label key of a labeled tab jump, driven from C# (TabJumper).
--- Sends back the picked label, or an empty string when Escape cancels.
function _G.vsneo._tab_jump_read()
  overlay_active(1)
  local ok, ch = pcall(vim.fn.getcharstr)
  overlay_active(false)
  vim.rpcnotify(chan, 'vsneo_tab_pick', (ok and ch ~= '\27') and ch or '')
end

local JUMP_LABELS = 'asdfghjklqwertyuiopzxcvbnm'

--- flash-style jump: s, type to narrow the visible matches, press the shown
--- label to land on it. <CR> lands on the nearest match, <BS> edits the
--- pattern, <Esc> cancels. Matching is plain and case-insensitive, over the
--- visible window only.
function _G.vsneo.jump()
  overlay_active(1)

  local function finish()
    overlay_labels({})
    overlay_active(false)   -- a boolean: 0 is truthy in Lua and would send 1
  end

  local function land(match)
    vim.cmd("normal! m'")   -- jumplist, so '' walks back
    vim.api.nvim_win_set_cursor(0, { match.line + 1, match.col })
    finish()
  end

  local pattern = ''
  while true do
    local matches = {}
    if #pattern > 0 then
      local cursor = vim.api.nvim_win_get_cursor(0)
      local needle = pattern:lower()
      for lnum = vim.fn.line('w0'), vim.fn.line('w$') do
        local text = vim.api.nvim_buf_get_lines(0, lnum - 1, lnum, false)[1] or ''
        local from = 1
        while true do
          local start = text:lower():find(needle, from, true)
          if not start then break end
          matches[#matches + 1] = { line = lnum - 1, col = start - 1 }
          from = start + 1
        end
      end
      table.sort(matches, function(a, b)
        local da = math.abs(a.line - (cursor[1] - 1)) * 10000 + math.abs(a.col - cursor[2])
        local db = math.abs(b.line - (cursor[1] - 1)) * 10000 + math.abs(b.col - cursor[2])
        return da < db
      end)
    end

    -- flash's rule: a label must not be a character that would extend the
    -- pattern to a real match, or typing it to narrow jumps instead - with
    -- eighteen or more matches for the first letter, typing 'o' used to
    -- land on match #18.
    local taken = {}
    for _, match in ipairs(matches) do
      local text = vim.api.nvim_buf_get_lines(0, match.line, match.line + 1, false)[1] or ''
      local after = text:sub(match.col + #pattern + 1, match.col + #pattern + 1):lower()
      if after ~= '' then taken[after] = true end
    end
    local labels = {}
    for i = 1, #JUMP_LABELS do
      local l = JUMP_LABELS:sub(i, i)
      if not taken[l] then labels[#labels + 1] = l end
    end

    -- [line, startByte, endByte, text]: empty text marks the whole match,
    -- text is the label box over its first character.
    local items = {}
    for i, match in ipairs(matches) do
      items[#items + 1] = { match.line, match.col, match.col + #pattern, '' }
      local label = labels[i] or ''
      if label ~= '' then
        match.label = label
        items[#items + 1] = { match.line, match.col, match.col + 1, label }
      end
    end
    overlay_labels(items)

    -- A unique match is already the answer; flash does not wait either.
    if #matches == 1 then
      land(matches[1])
      return
    end

    local ok, ch = pcall(vim.fn.getcharstr)
    if not ok or ch == '\27' then finish() return end
    if ch == '\r' then
      if matches[1] then land(matches[1]) else finish() end
      return
    end
    if ch == '\128kb' then                  -- <BS>
      pattern = pattern:sub(1, -2)
      if pattern == '' then finish() return end
    elseif #ch == 1 then                    -- a printable ASCII byte
      for _, match in ipairs(matches) do
        if match.label == ch then land(match) return end
      end
      pattern = pattern .. ch
    else
      finish()                              -- any other special key cancels
      return
    end
  end
end

