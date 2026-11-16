-- A Vimscript double-quoted string for any byte string, pure ASCII.
local function mr_vim_string(text)
  local out = { '"' }
  for i = 1, #text do
    local b = text:byte(i)
    if b == 92 then out[#out + 1] = '\\\\'
    elseif b == 34 then out[#out + 1] = '\\"'
    elseif b == 10 then out[#out + 1] = '\\n'
    elseif b == 9 then out[#out + 1] = '\\t'
    elseif b < 32 or b >= 127 then out[#out + 1] = string.format('\\x%02x', b)
    else out[#out + 1] = string.char(b) end
  end
  out[#out + 1] = '"'
  return table.concat(out)
end

vim.api.nvim_create_autocmd('RecordingEnter', {
  group = group,
  callback = function()
    mr = { reg = vim.fn.reg_recording(), log = {}, sessions = {}, open = nil }
  end,
})

vim.api.nvim_create_autocmd('ModeChanged', {
  group = group,
  pattern = '*:*',
  callback = function()
    -- A '.' replay inside a recording writes its text over the API; the
    -- register holds the '.', which replays it again. Nothing to capture.
    if mr == nil or dr.replaying then return end
    local old, new = vim.fn.expand('<amatch>'):match('^([^:]*):(.*)$')
    if old == nil then return end

    if new == 'i' and old ~= 'i' then
      if mr.open ~= nil then ins_end(mr.open.ins) end  -- a session whose leave never came
      mr.open = { s = #mr.log, ins = ins_begin() }
      return
    end

    if old == 'i' and new ~= 'i' and mr.open ~= nil then
      local session = mr.open
      mr.open = nil
      local last = mr.log[#mr.log]
      if last ~= nil and #mr.log > session.s and MR_EXIT_KEYS[last] then
        session.e = #mr.log          -- the exit key, kept in the register
      else
        session.e = #mr.log + 1      -- left without a key: add an <Esc>
        session.add_esc = true
      end
      mr.sessions[#mr.sessions + 1] = session
      session.text = ins_end(session.ins)  -- nil when it cannot be trusted
      session.ins = nil
    end
  end,
})

local function mr_finish(rec)
  local reg = rec.reg:lower()
  local plain = table.concat(rec.log)
  local current = vim.fn.getreg(reg)
  if plain == '' or current:sub(-#plain) ~= plain then
    -- The log does not reproduce what nvim recorded: leave it untouched.
    return
  end

  local out, i, lost = {}, 1, false
  for _, sn in ipairs(rec.sessions) do
    for k = i, math.min(sn.s, #rec.log) do out[#out + 1] = rec.log[k] end
    local inner_end = math.min(sn.e - 1, #rec.log)
    if sn.text == nil then
      -- Keep whatever keys the session had; its typed text is lost.
      for k = sn.s + 1, inner_end do out[#out + 1] = rec.log[k] end
      lost = true
    elseif sn.text ~= '' then
      out[#out + 1] = '\18\15=' .. mr_vim_string(sn.text) .. '\r'
    end
    if sn.add_esc then out[#out + 1] = '\27' end
    i = sn.e
  end
  for k = i, #rec.log do out[#out + 1] = rec.log[k] end

  vim.fn.setreg(reg, current:sub(1, #current - #plain) .. table.concat(out), 'c')
  if lost then
    vim.api.nvim_echo({ { 'macro: inserted text not captured for one insert (rewritten around it, or too large)',
      'WarningMsg' } }, true, {})
  end
end

vim.api.nvim_create_autocmd('RecordingLeave', {
  group = group,
  callback = function()
    local rec = mr
    mr = nil
    if rec == nil then return end
    -- Stopped from inside insert (<C-o>q): that session never gets its leave.
    if rec.open ~= nil then ins_end(rec.open.ins) end
    if #rec.sessions == 0 then return end
    -- The stop key ('q') is logged but never part of the register.
    table.remove(rec.log)
    -- nvim writes the register after this event, so the rewrite waits a turn.
    vim.schedule(function() mr_finish(rec) end)
  end,
})

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
-- the pattern cannot loop (target -> targetX terminates). The user's match
-- is skipped by its original span; a change outside every span skips its
-- whole origin line. One-shot: the next captured change consumes the arming.
-- Undo is per replay pass, not one atomic step.
------------------------------------------------------------------
