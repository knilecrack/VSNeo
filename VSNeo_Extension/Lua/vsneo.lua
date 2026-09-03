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
-- config; that is separate, opt-in (~/.vsneorc), and sourced at the bottom of
-- this file.
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

-- Set around a cursor move Visual Studio must not echo back to its caret:
-- note_viewport clamps nvim's cursor into the window while the real caret is
-- scrolled off it, and that bookkeeping is not a motion. Consumed by the next
-- push, whichever autocmd makes it - CursorMoved may fire inside the API call
-- or on a later event-loop turn, depending on the embed.
local synthetic_cursor = false

-- Set by vsneo.set_cursor around a caret push from Visual Studio. Setting the
-- cursor scrolls nvim's window to reveal it when the target sits outside the
-- window, and that scroll is transitional - the topline Visual Studio is
-- actually showing arrives through note_viewport a moment later. While set,
-- push() reports -1 as the topline: "no scroll information in this push".
local scroll_silent = false

local function push()
  local ok, pos = pcall(vim.api.nvim_win_get_cursor, 0)
  if not ok then return end

  local syn = synthetic_cursor
  synthetic_cursor = false

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

  -- In blockwise visual, $ reaches the end of EVERY line, which no rectangle
  -- can describe. Vim records that state as curswant == v:maxcol, so the
  -- extension can draw the ragged block instead of the corner-to-corner box.
  local to_eol = kind == '\22' and vim.fn.getcurpos()[5] == vim.v.maxcol

  -- row is 1-based from nvim and 0-based everywhere in the extension;
  -- col is already a 0-based byte offset, which is what ColumnMapper wants.
  -- line('w0') is the first visible line: zz, zt, zb and <C-e> move only
  -- this and never the cursor, so without it they are invisible.
  local w0 = scroll_silent and -1 or (vim.fn.line('w0') - 1)
  vim.rpcnotify(chan, 'vsneo_state',
    m, pos[1] - 1, pos[2], w0, aline, acol, to_eol, syn)
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

  -- Byte column where i_CTRL-W would stop, computed without touching the
  -- cursor. Visual Studio performs the deletion itself (see
  -- VsNeoKeyProcessor.DeleteWordBackward): nvim's insert-mode cursor cannot
  -- be pushed onto the caret reliably enough to delete from - a push to
  -- one-past-the-end is clamped when the next key is processed - so nvim's
  -- only job here is the word semantics, 'iskeyword' included. row is
  -- 1-based, col a 0-based byte offset; the result is a 0-based byte column.
  word_back_boundary = function(row, col)
    local line = vim.api.nvim_buf_get_lines(0, row - 1, row, false)[1] or ''
    local before = vim.fn.strcharpart(line, 0, vim.fn.charidx(line, col))
    local n = vim.fn.strchars(before)
    local function ch(i) return vim.fn.strcharpart(before, i - 1, 1) end
    local function iskw(c) return vim.fn.match(c, [[\k]]) == 0 end
    -- Whitespace in front of the caret goes with the word before it, same
    -- as the real i_CTRL-W.
    while n > 0 and ch(n):match('%s') do n = n - 1 end
    if n > 0 then
      local kw = iskw(ch(n))
      while n > 0 and not ch(n):match('%s') and iskw(ch(n)) == kw do
        n = n - 1
      end
    end
    return vim.fn.byteidx(line, n)
  end,

  -- A mouse drag selected text in Visual Studio, where nvim never saw the
  -- keys. Rebuild that selection here as a charwise visual one, so the next
  -- operator applies to what is on screen. Rows are 1-based, columns 0-based
  -- byte offsets; both ends are INCLUSIVE, Vim-style (the caller has already
  -- pulled Visual Studio's exclusive end one character in). Any visual mode
  -- already active is left first: 'v' from visual would exit instead of
  -- re-anchoring. Verified against nvim 0.12: 'normal! v' enters charwise
  -- visual synchronously, and win_set_cursor from there extends it.
  visual_select = function(arow, acol, crow, ccol)
    local m = vim.api.nvim_get_mode().mode
    if m:match('^[vV\22]') then
      local esc = vim.api.nvim_replace_termcodes('<Esc>', true, false, true)
      vim.api.nvim_feedkeys(esc, 'nx', false)
    end
    vim.api.nvim_win_set_cursor(0, { arow, acol })
    vim.cmd('normal! v')
    vim.api.nvim_win_set_cursor(0, { crow, ccol })
    push()
  end,

  -- Visual Studio moved its caret (gd, Ctrl+-, a mouse click, insert-mode
  -- typing that scrolled the view) and nvim's cursor must follow. Row is
  -- 1-based, col a 0-based byte offset - nvim_win_set_cursor's convention.
  --
  -- Not the raw API: nvim_win_set_cursor scrolls the window to reveal the
  -- cursor when the pushed caret sits outside it, and the WinScrolled report
  -- of that transitional scroll used to reach Visual Studio before
  -- note_viewport's winrestview with the real topline. The view yanked to
  -- nvim's minimal scroll on every VS-initiated jump, and the correction was
  -- then swallowed by the echo ring because its value matched what
  -- note_viewport had just sent - leaving the caret off the part of the file
  -- on screen. The topline is note_viewport's to send; pushes fired from
  -- inside this call report none. The CursorMoved echo itself must keep
  -- flowing: CursorSynchronizer's buffer-switch settle window ends early on it.
  set_cursor = function(row, col)
    scroll_silent = true
    local ok, err = pcall(vim.api.nvim_win_set_cursor, 0, { row, col })
    scroll_silent = false
    if not ok then error(err, 0) end
  end,

  -- Visual Studio's real viewport, 1-based lines. Sent on every scroll,
  -- including the ones an nvim window cannot represent: an nvim window always
  -- contains its own cursor, so a topline that would hide the cursor is
  -- refused - and H/M/L then aimed at wherever you scrolled FROM.
  --
  -- The fix is Vim's own rule: the cursor never leaves the window. When the
  -- caret is scrolled off screen the cursor is clamped to the window edge
  -- nearest the caret (and rejoined with the caret the moment it is visible
  -- again). The push carrying that clamp is flagged synthetic, so Visual
  -- Studio's caret stays where the user left it - but H, M, L, zz, <C-d> and
  -- the next real motion all compute against what is actually on screen.
  --
  -- Skipped in visual/select mode, where the cursor is one end of the
  -- selection and clamping it would reshape the selection, and on the
  -- command line, where an 'incsearch' match IS the cursor.
  note_viewport = function(topline, height, caretline, caretcol)
    local k = vim.api.nvim_get_mode().mode:sub(1, 1)
    if k == 'v' or k == 'V' or k == '\22'
       or k == 's' or k == 'S' or k == '\19' or k == 'c' then
      return
    end

    local last = vim.fn.line('$')
    if topline > last then topline = last end
    if caretline > last then caretline = last end
    local botline = math.min(topline + height - 1, last)

    local row = caretline
    if row < topline then row = topline end
    if row > botline then row = botline end

    local cur = vim.api.nvim_win_get_cursor(0)
    local text = vim.api.nvim_buf_get_lines(0, row - 1, row, false)[1] or ''
    local maxcol = #text - 1
    if maxcol < 0 then maxcol = 0 end
    -- Rejoining the caret takes its exact column; a clamp keeps the current
    -- one, line length permitting.
    local col = row == caretline and caretcol or cur[2]
    if col > maxcol then col = maxcol end

    if row ~= cur[1] or col ~= cur[2] then
      synthetic_cursor = true
      vim.api.nvim_win_set_cursor(0, { row, col })
      -- CursorMoved fired inside the call has already consumed the latch;
      -- deferred, it has not - push now and the late push dedupes against it.
      if synthetic_cursor then push() end
    end

    vim.fn.winrestview({ topline = topline })
  end,

  -- Digest of the whole buffer for BufferMirror's settle check, as
  -- [sha256, lineCount]. The '\n' join mirrors how the C# side joins its
  -- lines, so equal hashes mean equal line arrays - and the settled case
  -- (nearly every pass) stays a tiny round trip instead of shipping every
  -- line of the file back over the pipe on each editing pause.
  buffer_hash = function(buf)
    local lines = vim.api.nvim_buf_get_lines(buf, 0, -1, false)
    return { vim.fn.sha256(table.concat(lines, '\n')), #lines }
  end,

  -- Register contents for the peek popup (RegistersPopup.cs), as
  -- [name, preview] pairs. nvim owns the registers, so one round trip
  -- collects them all rather than a getreg per register. Previews are
  -- flattened to a single line and capped; the popup is a reminder, not
  -- a full register editor.
  registers = function()
    local names = { '"', '-', '.', ':', '/', '%', '+', '*' }
    for c = string.byte('0'), string.byte('9') do names[#names + 1] = string.char(c) end
    for c = string.byte('a'), string.byte('z') do names[#names + 1] = string.char(c) end

    local out = {}
    for _, name in ipairs(names) do
      local ok, value = pcall(vim.fn.getreg, name)
      if ok and type(value) == 'string' and value ~= '' then
        local preview = value:gsub('%s+$', ''):gsub('\n', '↵')
        if #preview > 80 then preview = preview:sub(1, 77) .. '...' end
        out[#out + 1] = { name, preview }
      end
    end
    return out
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

-- Folding is Visual Studio outlining; the fold state lives there and is never
-- mirrored into nvim. zR is only an approximation: VS has no unconditional
-- "expand all" command, and ToggleAllOutlining collapses when regions are in
-- a mixed state.
act('za', 'Edit.ToggleOutliningExpansion')
act('zR', 'Edit.ToggleAllOutlining')
act('zM', 'Edit.CollapseToDefinitions')

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
    vim.cmd("normal! m'")   -- jumplist, so <C-o> walks back
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

    -- [line, startByte, endByte, text]: empty text marks the whole match,
    -- text is the label box over its first character.
    local items = {}
    for i, match in ipairs(matches) do
      items[#items + 1] = { match.line, match.col, match.col + #pattern, '' }
      local label = JUMP_LABELS:sub(i, i)
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

-- The native s is cl's synonym, so this mapping costs nothing; unmap or
-- rebind it from ~/.vsneorc if it is in the way.
vim.keymap.set('n', 's', function() _G.vsneo.jump() end,
  { silent = true, desc = 'VSNeo: jump to a visible match' })

--- flash-style f/F/t/T: read the target char, and when the line holds
--- several matches in that direction, label them and let the label key
--- pick one. Zero or one match never shows labels, and the landing is
--- always the native motion (fed with a count), so ; and , keep working.
local function jump_char(key)
  local ok, ch = pcall(vim.fn.getcharstr)
  -- Anything that is not one printable byte (Escape, arrows, multibyte)
  -- cannot be labeled: hand the whole thing back to the native motion.
  if not ok or #ch ~= 1 or ch:byte() < 32 then
    if ok then vim.api.nvim_feedkeys(key .. ch, 'n', false) end
    return
  end

  local forward = key == 'f' or key == 't'
  local cursor = vim.api.nvim_win_get_cursor(0)
  local line = vim.api.nvim_get_current_line()

  -- Match byte columns in motion order (nearest first), each paired with
  -- the count the native motion needs to reach it.
  local matches = {}
  if forward then
    local from = cursor[2] + 2   -- 1-based start just past the cursor
    local count = 0
    while true do
      local s = line:find(ch, from, true)
      if not s then break end
      count = count + 1
      matches[#matches + 1] = { col = s - 1, count = count }
      from = s + 1
    end
  else
    local found = {}
    local from = 1
    while true do
      local s = line:find(ch, from, true)
      if not s or s - 1 >= cursor[2] then break end
      found[#found + 1] = s - 1
      from = s + 1
    end
    for i = #found, 1, -1 do
      matches[#matches + 1] = { col = found[i], count = #found - i + 1 }
    end
  end

  if #matches == 0 then return end
  if #matches == 1 then
    vim.api.nvim_feedkeys(key .. ch, 'n', false)
    return
  end

  overlay_active(1)
  local items = {}
  for i, match in ipairs(matches) do
    local label = JUMP_LABELS:sub(i, i)
    if label ~= '' then
      match.label = label
      items[#items + 1] = { cursor[1] - 1, match.col, match.col + 1, label }
    end
  end
  overlay_labels(items)

  local ok2, pick = pcall(vim.fn.getcharstr)
  overlay_labels({})
  overlay_active(false)

  if not ok2 or pick == '\27' then return end   -- cancel: no jump at all

  for _, match in ipairs(matches) do
    if match.label == pick then
      local count = match.count > 1 and tostring(match.count) or ''
      vim.api.nvim_feedkeys(count .. key .. ch, 'n', false)
      return
    end
  end

  -- Not a label: behave as if the labels never appeared - native motion to
  -- the first match, then the key gets its normal meaning.
  vim.api.nvim_feedkeys(key .. ch, 'n', false)
  vim.api.nvim_feedkeys(pick, 'n', false)
end

for _, key in ipairs({ 'f', 'F', 't', 'T' }) do
  vim.keymap.set('n', key, function() jump_char(key) end,
    { silent = true, desc = 'VSNeo: ' .. key .. ' with jump labels' })
end

------------------------------------------------------------------
-- Search highlights (hlsearch)
--
-- nvim owns the pattern and the regex engine; Visual Studio owns the
-- pixels. We ask nvim for every match of getreg('/') and send the
-- positions over RPC so the extension can draw them. Keeping the regex
-- here means Vim's own syntax (\v, \c, \<, etc.) works unchanged.
------------------------------------------------------------------

local last_search_pattern = nil

-- vim.defer_fn schedules, it does not debounce: every trigger in a burst
-- (mirrored typing fires TextChanged per keystroke) used to stack its own
-- full O(buffer) rescan on nvim's single-threaded main loop - the same loop
-- that processes the incoming mirror edits and nvim_input. Stopping the
-- pending timer before rescheduling turns a burst into one scan per pause.
local search_timers = {}
local function schedule_search_scan(key, ms, fn)
  local t = search_timers[key]
  if t and not t:is_closing() then
    t:stop()
    t:close()
  end
  search_timers[key] = vim.defer_fn(fn, ms)
end

local function send_search_matches(force, pattern_override)
  local pattern
  if pattern_override ~= nil then
    -- While the search cmdline is open, the partial pattern lives in
    -- getcmdline(); getreg('/') still holds the *previous* search until <CR>
    -- lands. The override is how incremental search gets live matches.
    pattern = pattern_override
  else
    -- Pattern and hlsearch state both live in nvim. Nothing to send means
    -- "clear the highlights", which is exactly what :nohlsearch should do.
    if vim.v.hlsearch == 0 then
      last_search_pattern = nil
      vim.rpcnotify(chan, 'vsneo_search_matches', {})
      return
    end

    pattern = vim.fn.getreg('/')
  end

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
    schedule_search_scan('leave', 50, function() send_search_matches(true) end)
  end,
})

-- While the search is being typed. CmdlineChanged fires per keystroke, which
-- CursorMoved does not: incsearch jumps the text cursor but the autocmd stays
-- silent for the small per-character steps (measured - one event for the whole
-- typing session). push() is called explicitly so the current-match highlight
-- on the C# side follows those jumps.
vim.api.nvim_create_autocmd('CmdlineChanged', {
  group = group,
  pattern = { '/', '?' },
  callback = function()
    schedule_search_scan('cmdline', 30, function()
      local t = vim.fn.getcmdtype()
      if t ~= '/' and t ~= '?' then return end -- cmdline closed meanwhile
      send_search_matches(false, vim.fn.getcmdline())
      push()
    end)
  end,
})

-- After edits and buffer switches the matches may have moved.
vim.api.nvim_create_autocmd({ 'TextChanged', 'TextChangedI', 'BufEnter' }, {
  group = group,
  callback = function()
    schedule_search_scan('edit', 100, function() send_search_matches(true) end)
  end,
})

-- * and # set the pattern without leaving a command line. CursorMoved is the
-- only signal they produce, and the pattern check inside keeps this cheap.
vim.api.nvim_create_autocmd('CursorMoved', {
  group = group,
  callback = function()
    schedule_search_scan('moved', 50, function() send_search_matches(false) end)
  end,
})

------------------------------------------------------------------
-- User configuration (~/.vsneorc)
--
-- The extension starts nvim with -u NORC, so a user's init.lua never loads;
-- this is the supported way in. vimscript rather than init.lua because the
-- audience is coming from VsVim and its .vsvimrc, and most of one ports
-- verbatim - including the :vsc lines, via the shim below.
------------------------------------------------------------------

-- VsVim's ':vsc Some.Command' works here too: user commands must start with
-- an uppercase letter, so the real command is :Vsc and a cmdline abbreviation
-- preserves the lowercase spelling. The position guard stops it rewriting a
-- 'vsc' that appears later in the line, e.g. inside :s/vsc/x/.
vim.api.nvim_create_user_command('Vsc', function(opts)
  _G.vsneo.cmd(opts.args)
end, { nargs = '+', desc = 'VSNeo: run a Visual Studio command' })
vim.cmd([[cnoreabbrev <expr> vsc (getcmdtype() == ':' && getcmdpos() <= 4) ? 'Vsc' : 'vsc']])

-- :e must never reach nvim itself: it would load the file into a buffer nvim
-- owns - Visual Studio never opens it, the mirror ignores its edits ("some
-- other document"), and the state pushes keep reporting positions from a file
-- nobody is showing. Worse, a later mirror for the same path hits E95 naming
-- its own buffer. Files are Visual Studio's to open. File.OpenFile focuses an
-- already-open document, so this doubles as :b; a bare :e reopens the current
-- file, which is where Visual Studio's own changed-on-disk prompt lives. The
-- bang is accepted and ignored: conflict decisions about the file belong to
-- Visual Studio. Same uppercase-plus-abbreviation shim as :Vsc, verified
-- against real nvim: the guards keep :s/e/x/ untouched.
vim.api.nvim_create_user_command('Edit', function(opts)
  local path = opts.args ~= '' and opts.args or vim.api.nvim_buf_get_name(0)
  if path == '' then return end
  -- Absolute, so a relative path resolves against nvim's cwd once, here,
  -- rather than against whatever directory Visual Studio happens to favour.
  _G.vsneo.cmd('File.OpenFile', vim.fn.fnamemodify(path, ':p'))
end, { nargs = '?', bang = true, complete = 'file', desc = 'VSNeo: open file in Visual Studio' })
vim.cmd([[cnoreabbrev <expr> e    (getcmdtype() == ':' && getcmdpos() <= 2) ? 'Edit' : 'e']])
vim.cmd([[cnoreabbrev <expr> edit (getcmdtype() == ':' && getcmdpos() <= 5) ? 'Edit' : 'edit']])

-- pcall: a broken rc must not abort the companion, or the re-assert below -
-- and with it the whole viewport contract - would silently not happen.
local rc = vim.fn.expand('~/.vsneorc')
if vim.fn.filereadable(rc) == 1 then
  local ok, err = pcall(vim.cmd, 'source ' .. vim.fn.fnameescape(rc))
  if not ok then
    -- Nothing renders vim.notify here; the cmdline margin does render
    -- ext_messages, and :messages keeps it for later.
    vim.notify('VSNeo: ~/.vsneorc failed: ' .. tostring(err), vim.log.levels.ERROR)
  end
end

-- The Lua twin, sourced after the vimscript rc. vimscript mappings cannot
-- carry a desc, and the which-key popup reads desc first - so this is where
-- mappings meant to show up with real names live (see examples/vsneorc.lua).
local luarc = vim.fn.expand('~/.vsneorc.lua')
if vim.fn.filereadable(luarc) == 1 then
  local ok, err = pcall(dofile, luarc)
  if not ok then
    vim.notify('VSNeo: ~/.vsneorc.lua failed: ' .. tostring(err), vim.log.levels.ERROR)
  end
end

-- These are invariants, not preferences (see the top of this file for why each
-- one matters): the viewport synchroniser, the mirrored buffer and the
-- invisible-chrome layout all assume them. A user rc runs after the initial
-- setup and could casually break any of them with a 'set scrolloff=10', so
-- they are asserted again, last, unconditionally.
vim.o.wrap = false
vim.o.scrolloff = 0
vim.o.sidescrolloff = 0
vim.o.laststatus = 0
vim.o.swapfile = false

------------------------------------------------------------------
-- Highlight groups as configuration
--
-- nvim renders nothing, but the extension draws search matches and the yank
-- flash as WPF adornments - and their colors come from here, so a ':hi Search
-- guibg=...' line in ~/.vsneorc really does change what Visual Studio shows.
-- Positional args with -1 for "unset": keeps the C# msgpack reader trivial.
-- Runs after the rc precisely so user definitions are the ones we read.
------------------------------------------------------------------

local function hl_bg(name)
  local ok, hl = pcall(vim.api.nvim_get_hl, 0, { name = name, link = false })
  if ok and hl and hl.bg then return hl.bg end
  return -1
end

local function send_highlights()
  local cur = hl_bg('CurSearch')
  if cur == -1 then cur = hl_bg('IncSearch') end
  vim.rpcnotify(chan, 'vsneo_highlights', hl_bg('Search'), cur, hl_bg('IncSearch'))
end

send_highlights()
vim.api.nvim_create_autocmd('ColorScheme', {
  group = group,
  callback = send_highlights,
})

------------------------------------------------------------------
-- Yank flash (LazyVim's 'highlight on yank', bridged)
--
-- TextYankPost also fires for deletions; only 'y' is a yank. Segments are
-- [line, startByte, endByte] triples like search matches, 0-based. The marks
-- are 1-based inclusive byte columns, so the end column gains the byte length
-- of its character to keep multibyte tails inside the flash.
------------------------------------------------------------------

vim.api.nvim_create_autocmd('TextYankPost', {
  group = group,
  callback = function()
    if vim.v.event.operator ~= 'y' then return end

    local s = vim.fn.getpos("'[")
    local e = vim.fn.getpos("']")
    if s[2] == 0 or e[2] == 0 then return end

    local linewise = vim.v.event.regtype:sub(1, 1) == 'V'
    local lines = vim.api.nvim_buf_get_lines(0, s[2] - 1, e[2], false)
    if #lines == 0 then return end

    local segments = {}
    for i, text in ipairs(lines) do
      local first, last
      if linewise then
        first, last = 0, #text
      else
        -- Charwise (and approximately blockwise): clamp the marks to this line.
        first = (i == 1) and (s[3] - 1) or 0
        last = #text
        if i == #lines then
          local ch = vim.fn.strcharpart(text, vim.fn.charidx(text, e[3] - 1), 1)
          last = math.min(e[3] - 1 + #ch, #text)
        end
      end
      if last > first then
        table.insert(segments, { s[2] - 1 + i - 1, first, last })
      end
    end

    if #segments > 0 then
      vim.rpcnotify(chan, 'vsneo_yank', segments)
    end
  end,
})
