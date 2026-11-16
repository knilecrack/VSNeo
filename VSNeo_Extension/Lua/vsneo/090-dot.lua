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

dot_forget = function() dr.change = nil end

mirror_wrote = function(buf, before, after)
  if buf == 0 then buf = vim.api.nvim_get_current_buf() end
  local change = dr.change
  if change ~= nil and change.buf == buf and change.tick == before then
    change.tick = after
  end
end

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

-- Text inserted during an insert session, read off the buffer when the
-- session ends.
--
-- The session keeps a bracket: two positions that start out together at the
-- insertion point and are carried through every buffer change while the
-- session is open (on_bytes, attached for the session only). Text written
-- at the bracket's end moves the end along; text written at its start stays
-- inside; changes before it shift both edges; changes after it touch
-- nothing. At insert leave the bracket spans exactly what the session
-- inserted - typed as keys or written by the mirror, wherever the cursor is.
--
-- The cursor is the one thing this must not depend on. The first version
-- read the slice from the cursor at insert entry to the settled cursor after
-- leave, and the text was only as good as that cursor: an insert mapping that
-- moves after leaving (imap <Down> <Esc><Down><Right>) captured a line and a
-- half, a caret push that reached nvim ahead of its edit captured one
-- character too many, a session with no caret push before <Esc> captured
-- nothing - '.' then deleted the next target and inserted nothing - and a
-- caret that jumped mid-insert captured whole spans of buffer (observed live:
-- a 92-line insertion per match, then mirror resync storms).
--
-- A change that replaces a span reaching across an edge of the bracket (the
-- mirror re-priming the buffer, a whole-line replacement) leaves no way to
-- say what was typed, and taints the session. A tainted session yields nil,
-- and so does one past the size limits below - a backstop, not the
-- mechanism: the change is then not repeatable, and says so.
--
-- One case stays ambiguous by nature: text written exactly at the bracket's
-- start counts as inserted, whoever wrote it (a formatter adding indent
-- there looks the same as the typist going back to the start).
local INS_MAX_TEXT_LINES = 50
local INS_MAX_TEXT_BYTES = 8000

local function ins_before(r1, c1, r2, c2)
  return r1 < r2 or (r1 == r2 and c1 < c2)
end

-- Opens a session at the cursor. Call on insert entry.
local function ins_begin()
  local buf = vim.api.nvim_get_current_buf()
  local cur = vim.api.nvim_win_get_cursor(0)
  local row, col = cur[1] - 1, cur[2]
  -- row/col: where the session started. from/to: the bracket, as {row, col}.
  local s = { buf = buf, row = row, col = col, from = { row, col }, to = { row, col } }
  local function taint() s.tainted = true end
  local ok = pcall(vim.api.nvim_buf_attach, buf, false, {
    -- A span starting at (srow, scol) was replaced. The old and new extents
    -- arrive relative to the start: rows as a count, the column absolute
    -- only when the extent ends on a later row.
    on_bytes = function(_, _, _, srow, scol, _, old_rows, old_col, _, new_rows, new_col)
      if s.done then return true end  -- session over: detach
      if s.tainted then return end
      local oer, oec = srow + old_rows, (old_rows == 0) and (scol + old_col) or old_col
      local ner, nec = srow + new_rows, (new_rows == 0) and (scol + new_col) or new_col
      -- A position at or behind the replaced span's end rides along with
      -- the text that follows the span.
      local function carried(p)
        if p[1] == oer then return { ner, nec + (p[2] - oec) } end
        return { p[1] + (ner - oer), p[2] }
      end
      local a, b = s.from, s.to
      if not ins_before(a[1], a[2], oer, oec) then
        -- The span ends at or before the start edge. Text written exactly
        -- there is the session's own; everything else - a deletion backing
        -- over what was there before, a rewritten indent - precedes it.
        local typed_here = old_rows == 0 and old_col == 0 and srow == a[1] and scol == a[2]
        if not typed_here then a = carried(a) end
      elseif ins_before(srow, scol, a[1], a[2]) then
        return taint()  -- starts before the edge and ends past it
      end
      if not ins_before(b[1], b[2], oer, oec) then
        b = carried(b)  -- includes text written exactly at the end edge
      elseif ins_before(srow, scol, b[1], b[2]) then
        return taint()
      end
      s.from, s.to = a, b
    end,
    on_reload = taint,
    on_detach = taint,
  })
  if not ok then taint() end
  return s
end

-- Closes a session. Returns the inserted text ('' when nothing was), or nil
-- and the reason when it cannot be trusted.
local function ins_end(s)
  s.done = true
  if s.tainted then return nil, 'the text around the insert was rewritten' end
  local a, b = s.from, s.to
  if not ins_before(a[1], a[2], b[1], b[2]) then return '' end
  if b[1] - a[1] + 1 > INS_MAX_TEXT_LINES then return nil, 'the insert is too large' end
  local ok, lines = pcall(vim.api.nvim_buf_get_text, s.buf, a[1], a[2], b[1], b[2], {})
  if not ok then return nil, 'the text around the insert was rewritten' end
  local text = table.concat(lines, '\n')
  if #text > INS_MAX_TEXT_BYTES then return nil, 'the insert is too large' end
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
      if dr.entry ~= nil then ins_end(dr.entry) end  -- a session whose leave never came
      dr.entry = ins_begin()
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
      -- Read here, not in a scheduled callback: when an insert mapping's rhs
      -- carries on after leaving (imap <F5> <Esc>dd), whatever it does next
      -- must find the change already recorded, with the tick it left behind.
      local text, why = ins_end(entry)
      local multi = dr_multi
      dr_multi = nil  -- one-shot, replayed or not
      if candidate.visual or candidate.keys == nil then
        dr.change = { visual = true }
        return
      end
      local ok, tick = pcall(vim.api.nvim_buf_get_changedtick, entry.buf)
      if not ok then
        dr.change = nil
        return
      end
      if text == nil then
        -- Rejected capture: replaying a guessed-at text is how buffers
        -- explode, and native '.' would replay the keys with no text at
        -- all. The change stays on record as unrepeatable, so '.' can
        -- refuse it instead.
        dr.change = { rejected = why, buf = entry.buf, tick = tick }
        vim.api.nvim_echo({ { 'dot-repeat: change not captured (' .. why .. ')', 'WarningMsg' } }, true, {})
        return
      end
      local change = { buf = entry.buf, tick = tick, keys = candidate.keys, text = text }
      dr.change = change
      if multi ~= nil and multi.buf == change.buf then
        -- The fan-out feeds keys, which an autocmd must not do mid-leave.
        vim.schedule(function()
          if dr.replaying or dr.change ~= change then return end
          dr_finish_multi(change, multi, { entry.row, entry.col })
          change.tick = vim.api.nvim_buf_get_changedtick(change.buf)
        end)
      end
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
  vim.fn.feedkeys(change.keys, 'x')
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
  -- pcall: an error out of feedkeys or set_text used to leave replaying
  -- latched, which switched key tracking, insert capture and macro capture
  -- off for the rest of the session.
  local ok, err = pcall(function()
    for _ = 1, times do
      if not dr_replay_once(change) then break end
    end
  end)
  dr.replaying = false
  if not ok then vim.notify('VSNeo: . failed: ' .. tostring(err), vim.log.levels.WARN) end
end

vim.keymap.set('n', '.', function()
  local change = dr.change
  dr.pending = {}   -- the '.' itself was tracked; it is not a change prefix
  dr.op_start = nil

  local here = vim.api.nvim_get_current_buf()
  local current = change ~= nil and here == change.buf
      and vim.api.nvim_buf_get_changedtick(here) == change.tick

  if current and change.rejected then
    -- The last change went through insert and its text was not captured.
    -- Native '.' would replay its keys with an empty insertion.
    vim.api.nvim_echo({ { 'dot-repeat: the last change was not captured (' .. change.rejected .. ')',
      'WarningMsg' } }, true, {})
    return
  end

  if not current or change.visual or change.keys == nil then
    -- Native dot: right for changes that never entered insert. With the
    -- count typed before it - fed bare, 3. after dd or gcc ran once.
    vim.fn.feedkeys((vim.v.count > 0 and tostring(vim.v.count) or '') .. '.', 'n')
    return
  end

  dr_replay(change, vim.v.count1)
  change.tick = vim.api.nvim_buf_get_changedtick(0)  -- the replay itself moved it
end, { silent = true, desc = 'VSNeo: repeat last change' })

------------------------------------------------------------------
-- Macros that keep their inserted text
--
-- The same passthrough hole as '.': a register records keys, and text typed
-- in Visual Studio never arrives as keys. qa cw <typing> <Esc> q recorded
-- "cw<Esc>", and @a deleted the next word and inserted nothing.
--
-- While a recording runs, the companion keeps its own log of the keys nvim
-- receives - the typed form from vim.on_key, which is exactly what the
-- register stores - and marks where each insert session starts and ends.
-- When the session ends, its inserted text is read back off the buffer,
-- through the same bracket '.' uses (ins_begin). When the recording
-- stops, each session's keys are replaced by that text and the register is
-- rewritten.
--
-- The text goes in as <C-r><C-o>= and a Vimscript string: CTRL-R CTRL-O
-- inserts literally with no auto-indent, so a recorded "\n    body" does
-- not pick up a second indent on replay, and the string literal escapes
-- every byte outside printable ASCII - a raw UTF-8 continuation byte 0x80
-- inside a register would be read back as the start of a special key.
--
-- Safety first: the rewrite happens only when the logged keys reproduce
-- nvim's own register byte for byte (minus the stop key). Anything this log
-- did not model - keys it could not see, an unusual stop - leaves nvim's
-- register exactly as recorded. Replace-mode sessions are left alone (this
-- inserts; it cannot overwrite), and so is a session whose text could not
-- be trusted (see ins_end) - that one echoes a warning.
------------------------------------------------------------------

-- Keys that end an insert session and must stay in the register: <Esc>,
-- <C-c>, and <C-o> (a one-command excursion that comes back to insert).
local MR_EXIT_KEYS = { ['\27'] = true, ['\3'] = true, ['\15'] = true }

local mr = nil  -- while recording: { reg, log = {typed keys}, sessions = {}, open = nil }

vim.on_key(function(_, typed)
  if mr == nil or typed == nil or typed == '' then return end
  mr.log[#mr.log + 1] = typed
end)

