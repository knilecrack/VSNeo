-- The jump mark ('') for a command that moves the caret Visual Studio-side.
-- :normal! over RPC flaps insert mode (i -> n -> i, with ModeChanged firing only
-- on the way out), so a mapping that calls this from insert would leave the
-- extension's mode cache at Normal. Skipped outside normal mode, as
-- SeekyCommands.RecordJump does.
local function record_jump()
  if vim.api.nvim_get_mode().mode == 'n' then vim.cmd("normal! m'") end
end

-- The line n screen rows below (n > 0) or above (n < 0) line l, a closed fold
-- counting as one row - the way the window, and Visual Studio's collapsed
-- regions that the folds mirror, count them. Stops at the buffer's ends.
local function rows_from(l, n)
  local last = vim.fn.line('$')
  local step = n > 0 and 1 or -1
  for _ = 1, math.abs(n) do
    local edge = step > 0 and vim.fn.foldclosedend(l) or vim.fn.foldclosed(l)
    if edge ~= -1 then l = edge end
    if (step > 0 and l >= last) or (step < 0 and l <= 1) then break end
    l = l + step
  end
  local start = vim.fn.foldclosed(l)
  return start ~= -1 and start or l
end

-- The Visual Studio operator armed by the key just typed (fresh while its
-- motion is owed), and the one that last ran. A dot replay runs g@ without
-- the mapping, so it takes `ran`: an armed operator abandoned with <Esc>
-- (gcj, then gs<Esc>, then .) used to make . run the abandoned command.
local vs_op = { armed = nil, ran = nil, fresh = false }

-- Leaving operator-pending ends the arming, run or abandoned. Scheduled: the
-- operatorfunc of a completed operator runs within the same key, first.
vim.api.nvim_create_autocmd('ModeChanged', {
  group = group,
  pattern = 'no*:*',
  callback = function() vim.schedule(function() vs_op.fresh = false end) end,
})

_G.vsneo = {
  channel = chan,

  -- A Visual Studio command as a Vim operator: gcap, gcc, =i{, ==. The
  -- returned function is an expr mapping's rhs; it arms the operatorfunc
  -- and returns g@, so nvim reads the motion or text object (count, dot
  -- repeat and visual mode included) and calls _operator with the range's
  -- '[ and '] marks. Nothing is selected on nvim's side: the range goes to
  -- the extension as line/byte positions, which selects it in the active
  -- view, runs the command and collapses the selection - the command's
  -- edits then come back through the mirror like any Visual Studio edit.
  --   vim.keymap.set({ 'n', 'x' }, 'gc', vsneo.operator('Edit.CommentSelection'), { expr = true })
  operator = function(command)
    return function()
      vs_op.armed, vs_op.fresh = command, true
      vim.o.operatorfunc = 'v:lua.vsneo._operator'
      return 'g@'
    end
  end,

  _operator = function(kind)
    local command = vs_op.fresh and vs_op.armed or vs_op.ran
    vs_op.fresh, vs_op.ran = false, command
    if not command then return end
    local s = vim.api.nvim_buf_get_mark(0, '[')
    local e = vim.api.nvim_buf_get_mark(0, ']')
    -- 1-based rows, 0-based byte columns, ']' inclusive; kind is
    -- 'line', 'char' or 'block' (block is treated as char: Visual Studio's
    -- commands take a stream selection).
    if dot_forget then dot_forget() end
    vim.rpcnotify(chan, 'vsneo_range_action', command, kind, s[1], s[2], e[1], e[2])
  end,

  cmd = function(name, args)
    vim.rpcnotify(chan, 'vsneo_action', name, args or '')
  end,

  -- A command that changes the selection - Edit.ExpandSelection and
  -- ContractSelection are the language service's semantic selection - run
  -- so that the selection it leaves becomes nvim's visual selection (the
  -- same hand-over a mouse drag gets). An emptied selection leaves visual.
  cmd_select = function(name)
    vim.rpcnotify(chan, 'vsneo_select_action', name)
  end,

  -- Same, but records the jump first. Visual Studio moves the caret
  -- itself, and nvim would see only a cursor move rather than a jump,
  -- leaving '' with nowhere to go back to.
  goto_cmd = function(name, args)
    record_jump()
    vim.rpcnotify(chan, 'vsneo_action', name, args or '')
  end,

  -- Opens the Seeky picker (files/grep/lines/symbols/outline/git/dirs, or
  -- resume to reopen the last one as it was left), embedded
  -- in the extension itself - no second extension to install, no IPC. (The
  -- standalone SeekyVS is an out-of-proc extension whose commands never
  -- surface in DTE.Commands, which is why this is a notification and not
  -- vsneo.cmd.) Records the jump first, as goto_cmd does: a pick lands far
  -- away, and '' should come back from it. A cancelled picker leaves one
  -- entry pointing at this line - harmless. Returns true so mappings written
  -- for the pipe era keep working.
  seeky = function(mode, query)
    record_jump()
    vim.rpcnotify(chan, 'vsneo_seeky', mode, query or '')
    return true
  end,

  -- Rows for the Seeky pickers only nvim can fill, fetched once per picker
  -- show (SeekyPickerController.NvimRowsAsync). Each row is { name, text,
  -- path?, line?, col?, preview? }: name is what the picker matches and what
  -- Enter acts on (the mark, the register, the keys, the history entry),
  -- text the detail beside it. Most recent first where there is an order.
  seeky_source = function(mode)
    local out = {}
    if mode == 'marks' then
      -- a-z of this buffer, then A-Z: the marks a ' or ` jump can reach.
      local cur = vim.api.nvim_get_current_buf()
      local function add(m)
        local buf = m.pos[1] ~= 0 and m.pos[1] or cur
        local path = m.file and vim.fn.fnamemodify(m.file, ':p')
          or vim.api.nvim_buf_get_name(buf)
        local text = ''
        if vim.api.nvim_buf_is_valid(buf) and vim.api.nvim_buf_is_loaded(buf) then
          text = vim.api.nvim_buf_get_lines(buf, m.pos[2] - 1, m.pos[2], false)[1] or ''
        end
        out[#out + 1] = { name = m.mark:sub(2), path = path, line = m.pos[2],
          col = m.pos[3], text = vim.trim(text) }
      end
      for _, m in ipairs(vim.fn.getmarklist(cur)) do
        if m.mark:match("^'[a-z]$") then add(m) end
      end
      for _, m in ipairs(vim.fn.getmarklist()) do
        if m.mark:match("^'[A-Z]$") then add(m) end
      end
    elseif mode == 'registers' then
      for _, r in ipairs(_G.vsneo.registers()) do
        local ok, full = pcall(vim.fn.getreg, r[1])
        out[#out + 1] = { name = r[1], text = r[2],
          preview = ok and type(full) == 'string' and vim.fn.strcharpart(full, 0, 4000) or r[2] }
      end
    elseif mode == 'keymaps' then
      -- Normal mode, the buffer's own first: Enter types the keys, and only a
      -- normal-mode mapping can be typed from where the picker returns to.
      local seen = {}
      local function add(map)
        local keys = vim.fn.keytrans(vim.api.nvim_replace_termcodes(map.lhs, true, true, true))
        if seen[keys] or keys:find('<Plug>', 1, true) or keys:find('<SNR>', 1, true) then return end
        seen[keys] = true
        local what = map.desc or map.rhs
        if not what or what == '' then what = map.callback and '<Lua function>' or '' end
        out[#out + 1] = { name = keys, text = what }
      end
      for _, map in ipairs(vim.api.nvim_buf_get_keymap(0, 'n')) do add(map) end
      for _, map in ipairs(vim.api.nvim_get_keymap('n')) do add(map) end
    elseif mode == 'command_history' or mode == 'search_history' then
      local kind = mode == 'command_history' and ':' or '/'
      local seen = {}
      for i = vim.fn.histnr(kind), 1, -1 do
        local entry = vim.fn.histget(kind, i)
        if entry ~= '' and not seen[entry] then
          seen[entry] = true
          out[#out + 1] = { name = entry, text = '' }
        end
      end
    end
    return out
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
    -- The caret can sit past nvim's copy of the line (the mirror is a
    -- keystroke behind): charidx returns -1 there, the boundary came out 0,
    -- and Visual Studio deleted back to the start of the line.
    if col > #line then col = #line end
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
    -- Visual, replace and operator-pending all leave through <Esc>; a mouse
    -- selection made in any of them re-anchors from normal mode.
    if m:match('^[vV\22R]') or m:sub(1, 2) == 'no' then
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
    -- Visual Studio's own move; the note_viewport behind it must not read
    -- the scroll as nvim's.
    vs_top = vim.fn.line('w0')
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
  -- Visual Studio scrolled its view on its own (an amplified edge jump while
  -- j or k is held) and nvim's window must follow, now: its stale topline
  -- would otherwise keep scrolling a line per key and drag the view back.
  -- 1-based. Only the topline - the cursor stays where the keys put it, and a
  -- topline that would leave the cursor off screen is refused (keys already
  -- moved it on; the next capture resyncs).
  -- Called by the extension right before nvim_ui_try_resize, on the same
  -- ordered stream. Visual Studio keeps its first visible line when the view
  -- changes height, and so does nvim ('splitkeep' topline) - except when the
  -- cursor would end up in the resized window's bottom 'scrolloff' rows or
  -- below it. 'topline' then moves the cursor up a line and records a jump,
  -- which Visual Studio copied: the showcmd margin appearing on d pulled the
  -- caret up, and dd or . deleted the wrong line. For that one resize nvim
  -- keeps the cursor and scrolls instead, as Vim does, and Visual Studio
  -- follows the scroll. 'cursor' for every resize is no answer: it re-scrolls
  -- the window whenever the height changes, cursor mid-window included.
  before_resize = function(height)
    local so = math.min(vim.wo.scrolloff, math.floor((height - 1) / 2))
    vim.o.splitkeep = vim.fn.winline() > height - so and 'cursor' or 'topline'
  end,

  set_topline = function(topline)
    local before = vim.fn.winsaveview().topline
    vim.fn.winrestview({ topline = topline })
    -- nvim moves a topline that would hide the cursor on its own; seeing any
    -- other first line than the one asked for (or its closed fold's start)
    -- means that happened, and the old window is the better answer.
    local want = vim.fn.foldclosed(topline)
    if want == -1 then want = topline end
    if vim.fn.line('w0') ~= want then
      vim.fn.winrestview({ topline = before })
    end
    vs_top = vim.fn.line('w0')
  end,

  --
  -- known_line/known_col (1-based line, 0-based byte column, optional) are
  -- nvim's cursor as Visual Studio last heard it when it captured this view,
  -- known_top (1-based, optional) its first visible line.
  -- If the cursor is anywhere else now, keys ran after the capture - most
  -- often because this request waited out a pending key sequence (nvim does
  -- not service RPC between the two keys of gg) - and applying it would drag
  -- the cursor and window back to where they were before those keys. Returns
  -- false when dropped for that reason, so the extension re-sends from fresh
  -- state; 'deferred' when the mode forbids moving the window (below), so it
  -- re-sends once the mode ends; true otherwise.
  note_viewport = function(topline, height, caretline, caretcol, caret_visible, known_line, known_col, known_top)
    if known_line ~= nil and known_line > 0 then
      local now = vim.api.nvim_win_get_cursor(0)
      if now[1] ~= known_line or now[2] ~= known_col then
        return false
      end
    end

    local mode = vim.api.nvim_get_mode().mode
    local k = mode:sub(1, 1)
    if mode:sub(1, 2) == 'no' or k == 'R'
       or k == 'v' or k == 'V' or k == '\22'
       or k == 's' or k == 'S' or k == '\19' or k == 'c' then
      -- Not 'true': the extension would count the topline as sent, and a
      -- wheel scroll in R left nvim's window behind for good - H/M/L and the
      -- next scroll report worked from the old one.
      return 'deferred'
    end

    -- The same for the window: zz, zt, zb, <C-e> and <C-y> scroll without
    -- moving the cursor, so the check above cannot see them. A request
    -- queued behind the first z (the showcmd margin resizes the view and
    -- flushes) ran right after zz, in the same event-loop turn as its
    -- WinScrolled - restoring the old topline before Visual Studio heard
    -- of the new one, or after, with the old one dropped there as an echo.
    -- zz did nothing. So the window must still be where Visual Studio last
    -- heard (known_top) or last put it (vs_top). Dropped once: the line then
    -- counts as known, so a scroll no push reports cannot refuse forever.
    local w0 = vim.fn.line('w0')
    if known_top ~= nil and known_top > 0 and w0 ~= known_top and w0 ~= vs_top then
      vs_top = w0
      return false
    end

    local last = vim.fn.line('$')
    if topline > last then topline = last end
    if caretline > last then caretline = last end

    -- Vim's rule for a scroll the user makes (<C-e>, the wheel): the window
    -- wins and the cursor moves, staying 'scrolloff' rows clear of the edges.
    -- Left where it was, nvim's own padding correction moved the window
    -- instead (winrestview below runs it) and reported a topline Visual Studio
    -- never sent, which scrolled it back: a wheel tick near the caret undone,
    -- or the two windows silently a line apart. Normal mode only: in insert
    -- the caret is Visual Studio's, and Visual Studio has no padding.
    local so = 0
    if k == 'n' then so = math.min(vim.wo.scrolloff, math.floor((height - 1) / 2)) end

    local row = caretline
    if not caret_visible or so > 0 then
      -- The window's extent in rows: a closed fold is one row, so
      -- 'topline + height - 1' put the bottom edge short of the truth whenever
      -- a fold was on screen, and the clamp dropped nvim's cursor on a line in
      -- the middle of the view.
      local botline = rows_from(topline, height - 1)
      local lo = topline > 1 and rows_from(topline, so) or 1
      -- No bottom padding once the window reaches the buffer's end - read off
      -- the fold's end, since rows_from answers a closed fold by its start
      -- (a class fold running to the last line counted as more to scroll).
      local bot_end = vim.fn.foldclosedend(botline)
      if bot_end == -1 then bot_end = botline end
      local hi = bot_end < last and rows_from(botline, -so) or last
      if lo > hi then lo = hi end
      if row < lo then row = lo end
      if row > hi then row = hi end
      -- Inside a closed fold, the fold's first line: that is the row shown.
      local fold_start = vim.fn.foldclosed(row)
      if fold_start ~= -1 then row = fold_start end
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
      -- Synthetic - Visual Studio's caret stays put - when the caret is off
      -- screen, or already where nvim's cursor is going (rejoining it). A
      -- padding move of a caret on screen is a real move, as <C-e>'s is in
      -- Vim: flagged synthetic, the caret stayed drawn on a line nvim had
      -- left, and dd deleted nvim's line, not the one under the caret.
      synthetic_cursor = not caret_visible or row == caretline
      vim.api.nvim_win_set_cursor(0, { row, col })
      -- CursorMoved fired inside the call has already consumed the latch;
      -- deferred, it has not - push now and the late push dedupes against it.
      if synthetic_cursor then push() end
    end

    vim.fn.winrestview({ topline = topline })
    vs_top = vim.fn.line('w0')
    return true
  end,

  -- Fold mirroring, Visual Studio -> nvim. The first argument of each is the
  -- document the state belongs to (see for_current_buffer). folds_set is the
  -- full rebuild, sent when a document is shown and as the drift healer; the
  -- other two are the incremental per-region changes raised by VS's
  -- outlining events. All three update the agreed copy, so a state push
  -- carrying the same change right back compares equal and is dropped.
  folds_set = function(path, list)
    if not for_current_buffer(path) then return end
    fit_ends(list)   -- deterministic, so an identical push still compares equal
    if not folds_dirty and folds_equal(list) and folds_exist(list) then
      -- No rebuild needed, but the resync still proves the boundaries
      -- current (an edit below every fold shifts nothing): re-arm detection.
      agreed_tick = vim.b.changedtick
      -- And it is the latest word: a different list deferred earlier in this
      -- insert session is obsolete, and Escape would apply it.
      if pending_folds and pending_folds.buf == vim.api.nvim_get_current_buf() then pending_folds = nil end
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
      pending_folds = { buf = vim.api.nvim_get_current_buf(), list = list, tick = vim.b.changedtick }
      return
    end
    apply_folds(list)
  end,

  fold_closed = function(path, s, e)
    if not for_current_buffer(path) then
      diag(('fold_closed %d-%d ignored: %s is not the current buffer %s'):format(s, e, tostring(path), vim.api.nvim_buf_get_name(0)))
      return
    end
    diag(('fold_closed %d-%d: before foldlevel %d foldclosed %d'):format(s, e, vim.fn.foldlevel(s), vim.fn.foldclosed(s)))
    vim.schedule(function()
      diag(('fold_closed %d-%d: after foldclosed %d, %d agreed regions'):format(s, e, vim.fn.foldclosed(s), #agreed_folds / 3))
    end)
    local raw_e = e
    e = fit_end(s, e, starts_of(agreed_folds))
    if e ~= raw_e then shared_line[s] = raw_e end
    local at = nil
    for i = 1, #agreed_folds, 3 do
      if agreed_folds[i] == s then at = i break end
    end
    if at ~= nil and agreed_folds[at + 2] then return end   -- our own change coming back
    -- One level, and only when the fold here is open. 'foldclose!' closes
    -- EVERY fold containing line s - the class's header line is also inside
    -- the namespace, so collapsing an inner region in Visual Studio closed
    -- the whole outer block, and the state push then reported every region
    -- inside it closed.
    if vim.fn.foldclosed(s) == -1 then
      local level = vim.fn.foldlevel(s)
      if level > 0 then vim.cmd(s .. 'foldclose') end
      local fc = vim.fn.foldclosed(s)
      if fc ~= -1 and fc ~= s then
        -- foldlevel > 0 only says s is inside a fold: one level closed the
        -- enclosing fold, which starts above s - a region nvim never got
        -- (Roslyn materialises a new method's region after the resync).
        -- Reopen the parent and create the region; ':fold' makes it closed.
        vim.cmd(s .. 'foldopen')
        vim.cmd(s .. ',' .. e .. 'fold')
      elseif level == 0 then
        vim.cmd(s .. ',' .. e .. 'fold')
      end
    end
    -- As in folds_set: record the state nvim actually reached; hidden under
    -- a closed ancestor, the requested state stands.
    local is_closed = region_state(s, true)
    if at ~= nil then
      agreed_folds[at + 1] = e
      agreed_folds[at + 2] = is_closed
    else
      agreed_folds[#agreed_folds + 1] = s
      agreed_folds[#agreed_folds + 1] = e
      agreed_folds[#agreed_folds + 1] = is_closed
    end
    -- agreed_tick is left alone: after an edit the other agreed boundaries
    -- are still pre-edit, and re-arming detection here reported them as
    -- vanished (VS then expanded them). Only folds_set re-arms.
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
    -- One level, only when this region is the closed one: 'foldopen!' opened
    -- its closed ancestors as well. Visual Studio expands a region only
    -- while its ancestors are expanded, so the fold at s is the only closed
    -- one containing s, and one level of 'foldopen' opens exactly it.
    if vim.fn.foldlevel(s) > 0 and vim.fn.foldclosed(s) == s then
      vim.cmd(s .. 'foldopen')
    end
    -- agreed_tick untouched, as in fold_closed: only folds_set re-arms.
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
  -- The mirror's drift check (BufferMirror.Verify): { sha256 of the lines
  -- joined with '\n', line count, changedtick }. known_tick is the tick of
  -- the last state both sides agreed on; while the buffer is still at it,
  -- nothing changed, and the answer is { '', line count, tick } without
  -- reading the lines at all.
  buffer_hash = function(buf, known_tick)
    local tick = vim.api.nvim_buf_get_changedtick(buf)
    if known_tick and known_tick >= 0 and tick == known_tick then
      return { '', vim.api.nvim_buf_line_count(buf), tick }
    end
    local lines = vim.api.nvim_buf_get_lines(buf, 0, -1, false)
    return { vim.fn.sha256(table.concat(lines, '\n')), #lines, tick }
  end,

  -- BufferMirror's write path. The echo guard needs the changedtick each
  -- write leaves behind, and fetching it separately was a second round trip
  -- per typed character: set_text, wait, get_changedtick, wait. Here the
  -- spans land and the tick comes back in one call. Spans are applied in the
  -- order given (the extension sends them last-first, so earlier offsets
  -- stay valid) and each is guarded on its own: one span addressing text
  -- nvim no longer has must not cost the others, and the tick still has to
  -- come back so the ones that did land are recognized as ours.
  -- Returns [changedtick, failedCount, firstError].
  apply_spans = function(buf, spans)
    local before = vim.api.nvim_buf_get_changedtick(buf)
    local failed, first_err = 0, ''
    for _, s in ipairs(spans) do
      local ok, err = pcall(vim.api.nvim_buf_set_text, buf, s[1], s[2], s[3], s[4], s[5])
      if not ok then
        failed = failed + 1
        if first_err == '' then first_err = tostring(err) end
      end
    end
    local tick = vim.api.nvim_buf_get_changedtick(buf)
    if mirror_wrote then mirror_wrote(buf, before, tick) end
    return { tick, failed, first_err }
  end,

  -- Whole-buffer replace (prime, drift repair, format-document), with the
  -- tick in the same reply for the same reason as apply_spans. Unlike a
  -- span, a failure here is not partial, so it raises.
  set_all_lines = function(buf, lines)
    local before = vim.api.nvim_buf_get_changedtick(buf)
    vim.api.nvim_buf_set_lines(buf, 0, -1, false, lines)
    -- Replacing every line takes nvim's manual folds with it. Forget the
    -- agreed regions so the full push the extension sends after a prime
    -- rebuilds them instead of comparing equal and skipping.
    if buf == 0 or buf == vim.api.nvim_get_current_buf() then
      agreed_folds = {}
      agreed_tick = -1
      folds_dirty = true
    end
    local tick = vim.api.nvim_buf_get_changedtick(buf)
    if mirror_wrote then mirror_wrote(buf, before, tick) end
    return tick
  end,

  -- Register contents for the peek popup (PeekPopup.cs), as
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
        -- Characters, not bytes: a byte cut splits a multibyte character
        -- (the ↵ above is three bytes) and sends an invalid string.
        if vim.fn.strchars(preview) > 80 then preview = vim.fn.strcharpart(preview, 0, 77) .. '...' end
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
        return vim.fn.strcharpart((lines[1]:gsub('^%s+', '')), 0, 60)
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

