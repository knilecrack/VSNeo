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

-- Visual Studio's outlining regions are mirrored here as manual folds (see
-- the fold section further down): manual means nvim never invents folds of
-- its own, and level 99 means the closed state is exactly what the sync says
-- it is rather than derived from a level. Any other foldmethod would
-- recompute folds nvim-side and the two editors would fight over them.
vim.wo.foldmethod = 'manual'
vim.wo.foldlevel = 99

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

-- Fold mirroring. Visual Studio's outlining regions are recreated here as
-- manual folds, and the closed state is kept identical on both sides. This is
-- the last agreed state as a flat {start, end, closed} triple list (1-based
-- lines, closed a boolean) - the echo guard: a push that changes nothing is
-- dropped, whichever side it originated from.
local agreed_folds = {}

-- changedtick at the last agreed-state sync. Line edits shift every fold
-- (both sides track them), but this list's boundaries stay where they were,
-- so comparing after an edit reports phantom opens and deletes - an edit
-- above a collapsed region expanded it. Detection stays silent until Visual
-- Studio's debounced resync refreshes the boundaries (and this tick).
local agreed_tick = -1

local function folds_equal(list)
  if #list ~= #agreed_folds then return false end
  for i = 1, #list do
    if list[i] ~= agreed_folds[i] then return false end
  end
  return true
end

-- Fold RPCs are sent around buffer switches; applying another document's
-- folds would corrupt this window's set until the next full sync. Compared
-- case-insensitively and slash-agnostically, matching the extension's own
-- path normalization.
local function normalize_path(p)
  return tostring(p):gsub('/', '\\'):lower()
end

local function for_current_buffer(path)
  if path == nil or path == '' then return false end
  return normalize_path(vim.api.nvim_buf_get_name(0)) == normalize_path(path)
end

-- nvim has no fold-changed event, so every push compares the actual state of
-- the agreed folds against the agreed copy: opened, closed, and deleted (zd)
-- all show up here with no per-command interception. The folds that still
-- EXIST are reported as {start, end, closed} triples; Visual Studio
-- reconciles its outlining from the list (a missing user fold means zd -
-- remove the region; a missing language fold means expand only). Its
-- answering fold pushes compare equal against the updated agreed copy and
-- no-op.
local function detect_fold_changes()
  if #agreed_folds == 0 then return end
  if vim.b.changedtick ~= agreed_tick then return end
  local actual = {}
  local kept = {}
  local changed = false
  for i = 1, #agreed_folds, 3 do
    local s, e, closed = agreed_folds[i], agreed_folds[i + 1], agreed_folds[i + 2]
    local exists = vim.fn.foldlevel(s) > 0
    local is_closed = exists and vim.fn.foldclosed(s) ~= -1
    if exists then
      actual[#actual + 1] = s
      actual[#actual + 1] = e
      actual[#actual + 1] = is_closed
      kept[#kept + 1] = s
      kept[#kept + 1] = e
      kept[#kept + 1] = is_closed
    end
    if not exists or is_closed ~= closed then changed = true end
  end
  if changed then
    agreed_folds = kept
    agreed_tick = vim.b.changedtick
    vim.rpcnotify(chan, 'vsneo_folds_changed', actual)
  end
end

-- A full fold rebuild is 'normal! zE' plus :fold commands - normal-mode
-- commands. Visual Studio's resync is debounced (FoldSynchronizer, 400 ms),
-- so it routinely arrives while nvim is still in insert mode: Enter inside
-- an outlining region shifts the region's end line, and the resync follows
-- the edit. Running :normal! over RPC while nvim is in insert flaps the
-- mode i -> n -> i, and ModeChanged fires on the way out but NOT on the way
-- back, so the last state push says "n" while nvim is actually back in
-- insert. The extension's mode cache then sticks at Normal until the next
-- keystroke - wrong badge, block caret (VS overtype), and an open nvim->VS
-- caret gate that snaps the caret onto nvim's lagging cursor. The rebuild
-- defers to the return to normal mode instead.
local pending_folds = nil   -- { buf = <bufnr>, list = { s, e, closed, ... } }

local function apply_folds(list)
  local view = vim.fn.winsaveview()
  vim.cmd('normal! zE')
  for i = 1, #list, 3 do
    local s, e, closed = list[i], list[i + 1], list[i + 2]
    if e >= s then
      vim.cmd(s .. ',' .. e .. 'fold')
      if not closed then vim.cmd(s .. 'foldopen!') end
    end
  end
  vim.fn.winrestview(view)
  -- nvim cannot close everything Visual Studio can (a one-line fold never
  -- closes: 'foldminlines'), so the agreed copy records the state nvim
  -- actually reached, not the requested one - otherwise every push would
  -- detect the unreachable closed state as a change and ping VS forever.
  for i = 1, #list, 3 do
    list[i + 2] = vim.fn.foldclosed(list[i]) ~= -1
  end
  agreed_folds = list
  agreed_tick = vim.b.changedtick
end

-- The deferred rebuild: on any transition back to normal mode, apply the
-- stashed list. A pending list whose buffer is no longer showing is dropped -
-- Visual Studio pushes a fresh full folds_set for whatever document it shows.
vim.api.nvim_create_autocmd('ModeChanged', {
  group = group,
  pattern = '*:*',
  callback = function()
    if pending_folds == nil then return end
    if vim.api.nvim_get_mode().mode:sub(1, 1) ~= 'n' then return end
    local pending = pending_folds
    pending_folds = nil
    if vim.api.nvim_get_current_buf() ~= pending.buf then return end
    apply_folds(pending.list)
  end,
})

local function push()
  detect_fold_changes()
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

-- Buffer identity rides ahead of the state push below: same-event autocmds
-- fire in creation order, so this one is created first, and msgpack-rpc
-- keeps the notification order on the wire. The extension needs it because
-- nvim can move its own window without Visual Studio asking - a file-mark
-- jump ('0-'9, 'A-'Z), :b, gf - and every cursor and
-- scroll report after that moment describes a buffer that is not on screen.
-- The extension's answer is to snap the window back (TextViewCreationListener);
-- Visual Studio owns which document is shown.
vim.api.nvim_create_autocmd('BufEnter', {
  group = group,
  callback = function()
    vim.rpcnotify(chan, 'vsneo_buf_enter', vim.api.nvim_buf_get_name(0))
    -- The agreed fold state belongs to the buffer that was just left;
    -- compared against the new one it produces phantom fold reports and
    -- swallows legitimate closes. Manual folds are window-local and do not
    -- survive the switch either, so nothing is lost: Visual Studio pushes a
    -- full folds_set for every document it shows. Resetting here - ahead of
    -- the push autocmd below, which runs detect_fold_changes - keeps the
    -- stale list from ever participating.
    agreed_folds = {}
    agreed_tick = -1
    pending_folds = nil
  end,
})

-- The buffer current at install time never fired the autocmd; report it so
-- the hub's idea of the shown buffer is never a stale null.
vim.rpcnotify(chan, 'vsneo_buf_enter', vim.api.nvim_buf_get_name(0))

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
  -- leaving '' with nowhere to go back to.
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
  -- Whether the caret is on screen is decided by Visual Studio
  -- (caret_visible), not by topline + height arithmetic: a collapsed
  -- outlining region compresses the view, so the last visible buffer line is
  -- NOT topline + height - 1, and arithmetic clamped cursors that were
  -- plainly visible - the next motion then snapped back to the window edge.
  --
  -- Skipped in visual/select mode, where the cursor is one end of the
  -- selection and clamping it would reshape the selection, and on the
  -- command line, where an 'incsearch' match IS the cursor.
  note_viewport = function(topline, height, caretline, caretcol, caret_visible)
    local k = vim.api.nvim_get_mode().mode:sub(1, 1)
    if k == 'v' or k == 'V' or k == '\22'
       or k == 's' or k == 'S' or k == '\19' or k == 'c' then
      return
    end

    local last = vim.fn.line('$')
    if topline > last then topline = last end
    if caretline > last then caretline = last end

    local row = caretline
    if not caret_visible then
      local botline = math.min(topline + height - 1, last)
      if row < topline then row = topline end
      if row > botline then row = botline end
    end

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

  -- Fold mirroring, Visual Studio -> nvim. The first argument of each is the
  -- document the state belongs to (see for_current_buffer). folds_set is the
  -- full rebuild, sent when a document is shown and as the drift healer; the
  -- other two are the incremental per-region changes raised by VS's
  -- outlining events. All three update the agreed copy, so a state push
  -- carrying the same change right back compares equal and is dropped.
  folds_set = function(path, list)
    if not for_current_buffer(path) then return end
    if folds_equal(list) then
      -- No rebuild needed, but the resync still proves the boundaries
      -- current (an edit below every fold shifts nothing): re-arm detection.
      agreed_tick = vim.b.changedtick
      return
    end
    -- 'normal! zE' is a normal-mode command; on the command line ('incsearch'
    -- included) it would misfire. Skipping leaves the previous folds in
    -- place - stale for the duration of one search, healed by the next push.
    local kind = vim.api.nvim_get_mode().mode:sub(1, 1)
    if kind == 'c' then return end
    -- In every other non-normal mode the rebuild defers to the return to
    -- normal mode (see pending_folds above): an insert-mode 'normal! zE'
    -- would flap the mode and corrupt the extension's mode cache.
    if kind ~= 'n' then
      pending_folds = { buf = vim.api.nvim_get_current_buf(), list = list }
      return
    end
    apply_folds(list)
  end,

  fold_closed = function(path, s, e)
    if not for_current_buffer(path) then return end
    local at = nil
    for i = 1, #agreed_folds, 3 do
      if agreed_folds[i] == s then at = i break end
    end
    if at ~= nil and agreed_folds[at + 2] then return end   -- our own change coming back
    if vim.fn.foldlevel(s) > 0 then
      vim.cmd(s .. 'foldclose!')
    else
      vim.cmd(s .. ',' .. e .. 'fold')
    end
    -- As in folds_set: record the state nvim actually reached.
    local is_closed = vim.fn.foldclosed(s) ~= -1
    if at ~= nil then
      agreed_folds[at + 1] = e
      agreed_folds[at + 2] = is_closed
    else
      agreed_folds[#agreed_folds + 1] = s
      agreed_folds[#agreed_folds + 1] = e
      agreed_folds[#agreed_folds + 1] = is_closed
    end
    agreed_tick = vim.b.changedtick
  end,

  fold_opened = function(path, s)
    if not for_current_buffer(path) then return end
    for i = 1, #agreed_folds, 3 do
      if agreed_folds[i] == s then
        if not agreed_folds[i + 2] then return end   -- our own change coming back
        agreed_folds[i + 2] = false
        break
      end
    end
    if vim.fn.foldlevel(s) > 0 then
      vim.cmd(s .. 'foldopen!')
    end
    agreed_tick = vim.b.changedtick
  end,

  -- BufferMirror adoption: nvim may already hold a buffer for a file Visual
  -- Studio is about to show (:b, gf, a plugin loaded it). Naming a fresh
  -- buffer the same path fails with E95, so the extension asks here first and
  -- adopts the existing one. Returns the buffer number, or 0.
  find_buffer = function(path)
    local wanted = normalize_path(path)
    for _, b in ipairs(vim.api.nvim_list_bufs()) do
      if normalize_path(vim.api.nvim_buf_get_name(b)) == wanted then return b end
    end
    return 0
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

  -- Mark list for the peek popup, as [name, preview] pairs like registers().
  -- Buffer marks (a-z) first, then file marks (A-Z) and numbered ones; the
  -- specials ('' ^ . [ ]) are navigation plumbing, not something you peek at.
  -- Previews are the marked line trimmed to 60 chars; a mark into a buffer
  -- that is not loaded falls back to file:line rather than reading from disk.
  marks = function()
    local function preview(buf, lnum, file)
      local ok, lines = pcall(vim.api.nvim_buf_get_lines, buf, lnum - 1, lnum, false)
      if ok and lines and lines[1] then
        return (lines[1]:gsub('^%s+', ''):sub(1, 60))
      end
      if file and file ~= '' then
        return vim.fn.fnamemodify(file, ':t') .. ':' .. lnum
      end
      return ''
    end

    local out = {}
    local function add(list, pattern)
      for _, m in ipairs(list) do
        local name = m.mark:sub(2) -- "'a" -> "a"
        local pos = m.pos          -- [bufnum, lnum, col, off]
        if pos[2] > 0 and name:match(pattern) then
          out[#out + 1] = { name, preview(pos[1], pos[2], m.file) }
        end
      end
    end
    add(vim.fn.getmarklist(vim.api.nvim_get_current_buf()), '^%l$')  -- a-z, current buffer
    add(vim.fn.getmarklist(), '^[%u%d]$')                            -- A-Z and 0-9, global
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
-- { buf, first, last }: the 1-based line range the last scan covered.
local last_search_range = nil

-- Only the lines around the window are scanned. Visual Studio draws matches
-- for its visible lines only, and a whole-buffer scan - every line copied
-- out and run through the Vimscript bridge - after every typing pause was
-- O(file) work on the main loop that also has to process the next key. The
-- margin (a window height, at least 200 lines) absorbs a VS view that shows
-- more than nvim's window (collapsed regions, a viewport sync in flight);
-- scrolling past it rescans (WinScrolled, CursorMoved below).
local function search_scan_range()
  local w0, w1 = vim.fn.line('w0'), vim.fn.line('w$')
  local margin = math.max(w1 - w0 + 1, 200)
  return w0, w1, math.max(1, w0 - margin),
         math.min(vim.api.nvim_buf_line_count(0), w1 + margin)
end

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
      last_search_range = nil
      vim.rpcnotify(chan, 'vsneo_search_matches', {})
      return
    end

    pattern = vim.fn.getreg('/')
  end

  if pattern == '' then
    last_search_pattern = nil
    last_search_range = nil
    vim.rpcnotify(chan, 'vsneo_search_matches', {})
    return
  end

  local buf = vim.api.nvim_get_current_buf()
  local w0, w1, first, last = search_scan_range()

  -- Same pattern, no edit, window still inside the scanned range: nothing
  -- changed. This keeps CursorMoved and WinScrolled cheap.
  local covered = last_search_range ~= nil
    and last_search_range[1] == buf
    and w0 >= last_search_range[2] and w1 <= last_search_range[3]
  if not force and pattern == last_search_pattern and covered then
    return
  end
  last_search_pattern = pattern

  -- A pattern that does not compile (for example while it is still being
  -- typed) has no matches to show, and matchstrpos would throw on it.
  if not pcall(vim.regex, pattern) then
    return
  end

  last_search_range = { buf, first, last }
  local lines = vim.api.nvim_buf_get_lines(buf, first - 1, last, false)
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
      table.insert(matches, { first + i - 2, s, e })
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

-- Scrolling beyond the scanned range needs the matches there; inside it the
-- range check in send_search_matches returns at once.
vim.api.nvim_create_autocmd('WinScrolled', {
  group = group,
  callback = function()
    schedule_search_scan('scrolled', 30, function() send_search_matches(false) end)
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

-- netrw cannot work here: its directory buffers are foreign buffers Visual
-- Studio can never show, and the snap-back would eat them. The plugin still
-- loads - plugins come from startup, before this script runs - so its
-- commands are overridden outright (force). The file explorer is Visual
-- Studio's Solution Explorer; SyncWithActiveDocument makes it select the
-- file being edited, which is what "explore from here" means. The split
-- variants collapse to the same target - Visual Studio has the one explorer.
local function solution_explorer()
  _G.vsneo.cmd('SolutionExplorer.SyncWithActiveDocument')
  _G.vsneo.cmd('View.SolutionExplorer')
end
for _, name in ipairs({ 'Explore', 'Ex', 'Vexplore', 'Sexplore', 'Hexplore', 'Texplore' }) do
  vim.api.nvim_create_user_command(name, solution_explorer,
    { force = true, desc = 'VSNeo: Visual Studio Solution Explorer' })
end

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
  local abs = vim.fn.fnamemodify(path, ':p')
  -- A directory is an explorer request (:e .), not a file open.
  if vim.fn.isdirectory(abs) == 1 then
    solution_explorer()
    return
  end
  _G.vsneo.cmd('File.OpenFile', abs)
end, { nargs = '?', bang = true, complete = 'file', desc = 'VSNeo: open file in Visual Studio' })
vim.cmd([[cnoreabbrev <expr> e    (getcmdtype() == ':' && getcmdpos() <= 2) ? 'Edit' : 'e']])
vim.cmd([[cnoreabbrev <expr> edit (getcmdtype() == ':' && getcmdpos() <= 5) ? 'Edit' : 'edit']])

-- :b resolves against nvim's buffer list but the target is opened in Visual
-- Studio. A buffer nvim loaded itself (:b with a path it read from disk) is
-- exactly what the mirror's adoption exists for, so this is safe either way.
-- Numbers and name substrings, like the real :b; a miss or a non-file buffer
-- is an error message (which ext_messages renders), never a desync.
vim.api.nvim_create_user_command('Buffer', function(opts)
  local arg = opts.args
  local byNumber = tonumber(arg)
  for _, b in ipairs(vim.api.nvim_list_bufs()) do
    local name = vim.api.nvim_buf_get_name(b)
    local hit = byNumber ~= nil and b == byNumber
      or (byNumber == nil and name ~= '' and name:lower():find(arg:lower(), 1, true) ~= nil)
    if hit then
      if name == '' then
        vim.api.nvim_err_writeln('E86: buffer ' .. arg .. ' is not a file Visual Studio can open')
        return
      end
      _G.vsneo.cmd('File.OpenFile', vim.fn.fnamemodify(name, ':p'))
      return
    end
  end
  vim.api.nvim_err_writeln('E86: buffer does not exist: ' .. arg)
end, { nargs = 1, complete = 'buffer', desc = 'VSNeo: open buffer in Visual Studio' })
vim.cmd([[cnoreabbrev <expr> b      (getcmdtype() == ':' && getcmdpos() <= 2) ? 'Buffer' : 'b']])
vim.cmd([[cnoreabbrev <expr> buffer (getcmdtype() == ':' && getcmdpos() <= 7) ? 'Buffer' : 'buffer']])

-- :bn/:bp walk Visual Studio's tabs, not nvim's buffer list - Visual Studio
-- is the window manager (same rule as the :split family), and its tab order
-- is the order you are looking at. The orders differ; the gesture matches.
vim.api.nvim_create_user_command('Bnext', function() _G.vsneo.cmd('Window.NextTab') end,
  { desc = 'VSNeo: next Visual Studio tab' })
vim.api.nvim_create_user_command('Bprevious', function() _G.vsneo.cmd('Window.PreviousTab') end,
  { desc = 'VSNeo: previous Visual Studio tab' })
vim.cmd([[cnoreabbrev <expr> bn        (getcmdtype() == ':' && getcmdpos() <= 3) ? 'Bnext' : 'bn']])
vim.cmd([[cnoreabbrev <expr> bnext    (getcmdtype() == ':' && getcmdpos() <= 6) ? 'Bnext' : 'bnext']])
vim.cmd([[cnoreabbrev <expr> bp        (getcmdtype() == ':' && getcmdpos() <= 3) ? 'Bprevious' : 'bp']])
vim.cmd([[cnoreabbrev <expr> bprevious (getcmdtype() == ':' && getcmdpos() <= 10) ? 'Bprevious' : 'bprevious']])

-- gf is deliberately NOT mapped: many configs bind it themselves (the sample
-- rc sends it to Edit.GoToFile), and native gf already lands on a real file
-- through the extension's follow logic - nvim loads the file, Visual Studio
-- opens or activates it. No key is stolen from nvim for it.

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
vim.wo.foldmethod = 'manual'
vim.wo.foldlevel = 99

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
-- Line number options
--
-- nvim's grid is never displayed, so 'number' and 'relativenumber'
-- render nothing here; they are configuration for the extension's
-- relative line number margin (RelativeLineNumberMargin.cs), which
-- reads them like Vim would. Pushed after the rc so user settings
-- are the ones we read, and again from OptionSet so ':set rnu' and
-- friends toggle the margin live. Both options are window-local;
-- the one-window model makes vim.wo the right read.
------------------------------------------------------------------

local function send_linenumbers()
  vim.rpcnotify(chan, 'vsneo_linenumbers',
    vim.wo.number and 1 or 0, vim.wo.relativenumber and 1 or 0)
end

send_linenumbers()
vim.api.nvim_create_autocmd('OptionSet', {
  group = group,
  pattern = { 'number', 'relativenumber' },
  callback = send_linenumbers,
})

------------------------------------------------------------------
-- Mapping table push (which-key data)
--
-- The extension renders pending-prefix hints itself; all it needs from here
-- is the mapping table, because user mappings are the point of the popup.
-- nvim_get_keymap reports lhs with <Leader> already expanded; the extension
-- normalizes <...> token casing and <Space> on its side, so the lhs crosses
-- the wire exactly as nvim reports it. <Plug> mappings are plugin plumbing,
-- never something to hint at. Buffer-local mappings are included: Visual
-- Studio shows exactly nvim's current buffer, so they always apply.
--
-- nvim has no "a mapping was added" event, so the table is refreshed on
-- every SourcePost (a lazy :packadd, a manual :source - debounced, one table
-- per plugin, not per file), on every BufEnter (buffer-local mappings are
-- per buffer), and through vsneo.keymaps_refresh() for mappings defined
-- interactively on the command line.
------------------------------------------------------------------

local function collect_keymaps(mode)
  local items = {}
  local seen = {}
  local function add(maps)
    for _, m in ipairs(maps) do
      if not m.lhs:find('<Plug>', 1, true) and not seen[m.lhs] then
        seen[m.lhs] = true
        table.insert(items, { m.lhs, m.desc or m.rhs or '' })
      end
    end
  end
  -- Buffer-local first: it shadows a global with the same lhs (that is what
  -- nvim itself does), and the extension dedupes the table first-wins, so a
  -- global added earlier would hide the buffer-local mapping's desc.
  add(vim.api.nvim_buf_get_keymap(0, mode))
  add(vim.api.nvim_get_keymap(mode))
  return items
end

local function send_keymaps()
  for _, mode in ipairs({ 'n', 'x' }) do
    vim.rpcnotify(chan, 'vsneo_keymaps', mode, collect_keymaps(mode))
  end
end

local keymaps_timer = nil
vim.api.nvim_create_autocmd('SourcePost', {
  group = group,
  callback = function()
    if keymaps_timer then keymaps_timer:stop() end
    keymaps_timer = vim.defer_fn(function()
      keymaps_timer = nil
      send_keymaps()
    end, 300)
  end,
})

-- Created after the vsneo_buf_enter autocmd at the top, so buffer identity
-- still rides ahead of everything else on the wire.
vim.api.nvim_create_autocmd('BufEnter', {
  group = group,
  callback = send_keymaps,
})

_G.vsneo.keymaps_refresh = send_keymaps

send_keymaps()

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

------------------------------------------------------------------
-- Macro recording indicator
--
-- The one piece of Vim state nothing else reports: msg_showmode does not
-- carry it and the state push has no field for it. The extension draws it as
-- a red badge next to the mode (ModeStatusBarItem.cs), noice-style.
--
-- The leave side pushes '' unconditionally rather than reading
-- reg_recording(): when the stop key arrives over nvim_input (the only way
-- keys arrive), nvim 0.12 fires RecordingLeave BEFORE clearing the register,
-- so the callback would push the register again and the badge would latch on.
------------------------------------------------------------------

vim.api.nvim_create_autocmd('RecordingEnter', {
  group = group,
  callback = function()
    vim.rpcnotify(chan, 'vsneo_recording', vim.fn.reg_recording())
  end,
})
vim.api.nvim_create_autocmd('RecordingLeave', {
  group = group,
  callback = function()
    vim.rpcnotify(chan, 'vsneo_recording', '')
  end,
})

------------------------------------------------------------------
-- Dot-repeat for changes whose text never reached nvim
--
-- Insert mode is passthrough: text typed in Visual Studio arrives through
-- the buffer mirror, never as keystrokes. nvim's redo record is
-- keystroke-based, so for any change that passes through insert (cw, cgn,
-- ci", o, A, ...) the record holds the operator and an EMPTY insertion,
-- and native '.' replays exactly that - deleting the next target while
-- inserting nothing, which is worse than doing nothing.
--
-- The companion reconstructs such changes itself:
--   * vim.on_key accumulates keys pressed in normal and operator-pending
--     mode. The ModeChanged n:no transition marks the operator's position
--     in that list, which is what separates the 'cw' that matters from
--     the 'jj' typed before it. A count directly before the operator
--     belongs to it; register prefixes are dropped (native '.' reuses
--     them, this replay does not).
--   * the inserted text is read back off the buffer: the slice from the
--     cursor at insert entry to the settled cursor after insert leave
--     (nvim parks it on the last inserted character, whether the text
--     arrived as keystrokes or over the API). Computed in a scheduled
--     callback because the cursor has not settled when ModeChanged fires.
--
-- '.' is then mapped to a replay that feeds the change keys - nvim's own
-- semantics find the target and perform the deletion - reads the
-- insertion point off the ModeChanged *:i that fires during the feed, and
-- writes the recorded text there with nvim_buf_set_text. It never enters
-- insert itself: feedkeys cannot hold insert once its input runs out, so
-- the whole replay runs in normal mode. With empty recorded text there is
-- nothing to write, and the fed keys' own insert exit has already settled
-- the cursor exactly like the original Esc.
--
-- Falls back to native '.' when there is no recorded insert-change: a
-- change that never entered insert (dd, x) replays natively just fine.
-- Native fallback also covers changes made from a visual selection (their
-- replay needs the selection, not just keys) and replace-mode sessions
-- (this replay inserts; it cannot overwrite). The recorded change is
-- discarded when the buffer it belongs to has moved on (changedtick) or
-- the active buffer is another one - some other edit owns redo then.
------------------------------------------------------------------

local dr = {
  pending = {},       -- keys seen in normal/operator-pending, oldest first
  op_start = nil,     -- index into pending of the in-flight operator key
  visual = false,     -- the in-flight change came from a visual selection
  entry = nil,        -- {row0, col0} cursor at insert entry
  candidate = nil,    -- {keys, visual} captured at insert entry
  change = nil,       -- last insert-change: {buf, tick, keys, text} or {visual=true}
  replay_entry = nil, -- {row0, col0} captured during a replay's insert flap
  replaying = false,  -- fed keys and their events must not be tracked
}

-- Multi-edit state, filled in by the section further down. Forward
-- declared: the ModeChanged autocmd below consumes the arming when the
-- change it was waiting for is captured.
local dr_multi = nil         -- armed: {buf, marks={...}}; nil when idle
local dr_finish_multi = nil  -- function assigned in the multi-edit section

local function dr_is_visual(mode)
  return mode == 'v' or mode == 'V' or mode == '\22'
end

vim.on_key(function(key)
  if dr.replaying then return end
  local mode = vim.api.nvim_get_mode().mode
  if mode:sub(1, 1) == 'n' or dr_is_visual(mode) then
    dr.pending[#dr.pending + 1] = key
    -- Motions pile up between changes; nothing this old can still matter.
    if #dr.pending > 64 then table.remove(dr.pending, 1) end
  end
end)

-- The keys that led into insert, without the motions typed before them.
local function dr_change_keys()
  local n = #dr.pending
  if n == 0 then return nil end
  local s = dr.op_start or n
  -- a count directly before the operator (or the insert key) belongs to it
  while s > 1 and #dr.pending[s - 1] == 1 and dr.pending[s - 1]:match('%d') do
    s = s - 1
  end
  if dr.pending[s] == '0' and s < n then s = s + 1 end  -- 0 is a motion, not a count
  return table.concat(dr.pending, '', s, n)
end

-- Text inserted during the session that started at {row0, col0}: the slice
-- up to the settled cursor, which insert leave parks on the last inserted
-- character. Empty when the cursor backed up to or past the entry point -
-- that is what an insert session with no text looks like.
--
-- The slice assumes the cursor moved only because text was typed. A caret
-- that moved for any other reason - a mouse click mid-insert, a VS-side
-- caret push racing this capture, navigation between Esc and the scheduled
-- capture - would make the "typed text" a whole span of buffer, and the
-- replay would splat it at every match (observed live: a 92-line insertion
-- per match, then mirror resync storms). A change that big is never a
-- dot-repeat candidate, so an oversized slice rejects the change entirely.
local DR_MAX_TEXT_LINES = 5
local DR_MAX_TEXT_BYTES = 500

local function dr_inserted_text(entry)
  local cur = vim.api.nvim_win_get_cursor(0)
  local er, ec = entry[1], entry[2]
  local xr, xc = cur[1] - 1, cur[2]
  if xr < er then return '' end
  if xr - er + 1 > DR_MAX_TEXT_LINES then return nil end
  local lines = vim.api.nvim_buf_get_lines(0, er, xr + 1, false)
  if #lines == 0 then return '' end
  -- inclusive 1-based end of the character under the cursor
  local last = lines[#lines]
  local e = xc + 1
  while e < #last and last:byte(e + 1) >= 0x80 and last:byte(e + 1) < 0xC0 do
    e = e + 1
  end
  if xr == er and e <= ec then return '' end
  local text
  if #lines == 1 then
    text = lines[1]:sub(ec + 1, e)
  else
    local parts = { lines[1]:sub(ec + 1) }
    for i = 2, #lines - 1 do parts[#parts + 1] = lines[i] end
    parts[#parts + 1] = last:sub(1, e)
    text = table.concat(parts, '\n')
  end
  if #text > DR_MAX_TEXT_BYTES then return nil end
  return text
end

-- 0-based byte column of a line's last character (the cursor wants the
-- character's start, not one past its final byte).
local function dr_last_char_col(line)
  local i = #line
  if i == 0 then return 0 end
  while i > 1 and line:byte(i) >= 0x80 and line:byte(i) < 0xC0 do
    i = i - 1
  end
  return i - 1
end

vim.api.nvim_create_autocmd('ModeChanged', {
  group = group,
  pattern = '*:*',
  callback = function()
    local old, new = vim.fn.expand('<amatch>'):match('^([^:]*):(.*)$')
    if old == nil then return end

    if dr.replaying then
      -- The replay's own insert flap: this is where the text has to go.
      if new == 'i' then
        local cur = vim.api.nvim_win_get_cursor(0)
        dr.replay_entry = { cur[1] - 1, cur[2] }
      end
      return
    end

    if new == 'i' then
      local cur = vim.api.nvim_win_get_cursor(0)
      dr.entry = { cur[1] - 1, cur[2] }
      dr.candidate = { keys = dr_change_keys(), visual = dr.visual or dr_is_visual(old) }
      dr.pending = {}
      dr.op_start = nil
      dr.visual = false
      return
    end

    if old == 'i' then
      local entry, candidate = dr.entry, dr.candidate
      dr.entry, dr.candidate = nil, nil
      if entry == nil or candidate == nil then return end
      vim.schedule(function()
        if dr.replaying then return end
        if candidate.visual or candidate.keys == nil then
          dr.change = { visual = true }
          dr_multi = nil  -- consumed; nothing replayable came of it
        else
          local text = dr_inserted_text(entry)
          if text == nil then
            -- rejected capture: replaying a guessed-at text is how buffers
            -- explode. Drop the change and the arming with it.
            dr.change = nil
            dr_multi = nil
            vim.api.nvim_echo({ { 'dot-repeat: change not captured (cursor moved during insert)', 'WarningMsg' } },
              true, {})
            return
          end
          dr.change = {
            buf = vim.api.nvim_get_current_buf(),
            tick = vim.api.nvim_buf_get_changedtick(0),
            keys = candidate.keys,
            text = text,
          }
          if dr_multi ~= nil and dr_multi.buf == dr.change.buf then
            dr_finish_multi(dr.change, dr_multi, entry)
            dr.change.tick = vim.api.nvim_buf_get_changedtick(0)
          end
          dr_multi = nil  -- one-shot, replayed or not
        end
      end)
      return
    end

    if old == 'n' and new:sub(1, 2) == 'no' then
      -- Every operator marks its own start, so 'ddcw' still resolves to
      -- 'cw': the second n:no transition overwrites the first.
      dr.op_start = #dr.pending
      return
    end
    if dr_is_visual(old) and new:sub(1, 2) == 'no' then
      dr.visual = true
      dr.op_start = #dr.pending
      return
    end
    if old:sub(1, 2) == 'no' and new == 'n' then
      dr.op_start = nil
      dr.visual = false
      return
    end
  end,
})

-- One replay pass: feed the change keys (nvim's own semantics find the
-- target and do the deletion), then write the recorded text at the
-- insertion point the fed keys entered insert at. False when the keys
-- found no target (cgn past the last match).
local function dr_replay_once(change)
  vim.fn.feedkeys(vim.api.nvim_replace_termcodes(change.keys, true, false, true), 'x')
  local entry = dr.replay_entry
  dr.replay_entry = nil
  if entry == nil then return false end
  if change.text ~= '' then
    local lines = vim.split(change.text, '\n', { plain = true })
    vim.api.nvim_buf_set_text(0, entry[1], entry[2], entry[1], entry[2], lines)
    local last_row = entry[1] + #lines - 1
    local base = (#lines == 1) and entry[2] or 0
    vim.api.nvim_win_set_cursor(0, { last_row + 1, base + dr_last_char_col(lines[#lines]) })
  end
  return true
end

local function dr_replay(change, times)
  dr.replaying = true
  for _ = 1, times do
    if not dr_replay_once(change) then break end
  end
  dr.replaying = false
end

vim.keymap.set('n', '.', function()
  local change = dr.change
  dr.pending = {}   -- the '.' itself was tracked; it is not a change prefix
  dr.op_start = nil

  if change == nil or change.visual or change.keys == nil
      or vim.api.nvim_get_current_buf() ~= change.buf
      or vim.api.nvim_buf_get_changedtick(0) ~= change.tick then
    -- Native dot: right for changes that never entered insert.
    vim.fn.feedkeys(vim.api.nvim_replace_termcodes('.', true, false, true), 'n')
    return
  end

  dr_replay(change, vim.v.count1)
  change.tick = vim.api.nvim_buf_get_changedtick(0)  -- the replay itself moved it
end, { silent = true, desc = 'VSNeo: repeat last change' })

------------------------------------------------------------------
-- Multi-edit: one change, replayed at every match
--
-- vsneo.multi_edit() arms the next insert-change: all matches of the last
-- search pattern (@/, so both /foo and * work) are stored as extmarks.
-- Change one match the usual way (cgn, type, Esc); when the change is
-- captured, it is replayed at every other stored position - cursor to the
-- extmark, change keys fed, recorded text written at the insertion point,
-- exactly the '.' replay. Extmarks track through the edits, so positions
-- stay honest as earlier replays shift the buffer, and iterating a stored
-- list instead of re-searching means a replacement that itself matches
-- the pattern cannot loop (target -> targetX terminates). The match the
-- user edited is skipped by its original span. One-shot: the next
-- captured change consumes the arming. Undo is per replay pass, not one
-- atomic step.
------------------------------------------------------------------

local dr_multi_ns = vim.api.nvim_create_namespace('vsneo_multi_edit')

function _G.vsneo.multi_edit()
  local pat = vim.fn.getreg('/')
  if pat == '' then
    -- No nvim search, nothing to arm. The classic cause: the matches were
    -- found with Visual Studio's Ctrl+F, which never touches @/ - and the
    -- cgn that follows then fails (E35) and the next typed keys land as
    -- normal-mode commands. Say so instead of arming nothing.
    vim.api.nvim_echo({ { 'multi-edit: no search pattern - search with / or * first', 'WarningMsg' } },
      true, {})
    return
  end
  local buf = vim.api.nvim_get_current_buf()
  vim.api.nvim_buf_clear_namespace(buf, dr_multi_ns, 0, -1)
  local marks = {}
  local save = vim.api.nvim_win_get_cursor(0)
  vim.api.nvim_win_set_cursor(0, { 1, 0 })
  local flags = 'cW'  -- 'c' once: a match starting at (1,0) is still a match
  local multiline = 0
  while #marks < 1000 do
    local s = vim.fn.searchpos(pat, flags)     -- no wraparound
    flags = 'W'
    if s[1] == 0 then break end
    local e = vim.fn.searchpos(pat, 'ceW')   -- end of the match at the cursor
    if e[1] == s[1] then
      marks[#marks + 1] = {
        id = vim.api.nvim_buf_set_extmark(buf, dr_multi_ns, s[1] - 1, s[2] - 1, {}),
        row = s[1] - 1, col = s[2] - 1, endcol = e[2] - 1,
      }
    else
      -- A match spanning lines cannot round-trip here: its replay deletes
      -- across the boundary, the next extmark collapses into the deletion,
      -- and every later pass eats another line (observed: a file consumed
      -- line by line, "lines 31-32 replaced by 1" twelve times over).
      multiline = multiline + 1
    end
  end
  vim.api.nvim_win_set_cursor(0, save)
  dr_multi = #marks > 0 and { buf = buf, marks = marks } or nil
  -- Arming is otherwise invisible; the count is the only feedback that the
  -- next change will fan out. Rides msg_show into MessageMargin.
  local msg = ('multi-edit: armed on %d matches'):format(#marks)
  if multiline > 0 then
    msg = msg .. (', %d multi-line skipped'):format(multiline)
  end
  vim.api.nvim_echo({ { msg } }, true, {})
end

-- Runs inside the capture schedule, right after the armed change has been
-- recorded. origin is the insertion point of the user's own edit (in the
-- pre-edit coordinates the arm-time spans are stored in).
dr_finish_multi = function(change, multi, origin)
  dr.replaying = true
  for _, m in ipairs(multi.marks) do
    -- the match the user edited themselves
    if not (m.row == origin[1] and origin[2] >= m.col and origin[2] <= m.endcol) then
      local pos = vim.api.nvim_buf_get_extmark_by_id(0, dr_multi_ns, m.id, {})
      if #pos > 0 then
        vim.api.nvim_win_set_cursor(0, { pos[1] + 1, pos[2] })
        vim.fn.feedkeys(vim.api.nvim_replace_termcodes(change.keys, true, false, true), 'x')
        local entry = dr.replay_entry
        dr.replay_entry = nil
        -- The fed keys must enter insert on the extmark's own line; anything
        -- else means the change found a different target than the stored
        -- match, and continuing would spray text across the buffer.
        if entry == nil or entry[1] ~= pos[1] then break end
        if change.text ~= '' then
          local lines = vim.split(change.text, '\n', { plain = true })
          vim.api.nvim_buf_set_text(0, entry[1], entry[2], entry[1], entry[2], lines)
          local last_row = entry[1] + #lines - 1
          local base = (#lines == 1) and entry[2] or 0
          vim.api.nvim_win_set_cursor(0, { last_row + 1, base + dr_last_char_col(lines[#lines]) })
        end
      end
    end
  end
  dr.replaying = false
  vim.api.nvim_buf_clear_namespace(multi.buf, dr_multi_ns, 0, -1)
end
