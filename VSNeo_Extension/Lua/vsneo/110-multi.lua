
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
  -- /\( fails with E54 but still sets @/: searchpos would throw after the
  -- walk moved the cursor to line 1, before winrestview.
  if not pcall(vim.regex, pat) then
    vim.api.nvim_echo({ { 'multi-edit: invalid search pattern ' .. pat, 'WarningMsg' } }, true, {})
    return
  end
  local buf = vim.api.nvim_get_current_buf()
  vim.api.nvim_buf_clear_namespace(buf, dr_multi_ns, 0, -1)
  local marks = {}
  -- winsaveview, not the cursor alone: nvim_win_set_cursor runs
  -- update_topline, so the walk below re-centred the window, WinScrolled
  -- pushed that, and Visual Studio's view jumped on arming.
  local save = vim.fn.winsaveview()
  vim.api.nvim_win_set_cursor(0, { 1, 0 })
  local flags = 'cW'  -- 'c' once: a match starting at (1,0) is still a match
  local multiline = 0
  local ok = pcall(function()
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
  end)
  vim.fn.winrestview(save)
  if not ok then
    vim.api.nvim_buf_clear_namespace(buf, dr_multi_ns, 0, -1)
    dr_multi = nil
    vim.api.nvim_echo({ { 'multi-edit: search failed for ' .. pat, 'WarningMsg' } }, true, {})
    return
  end
  dr_multi = #marks > 0 and { buf = buf, pat = pat, marks = marks } or nil
  -- Arming is otherwise invisible; the count is the only feedback that the
  -- next change will fan out. Rides msg_show into MessageMargin.
  local msg = ('multi-edit: armed on %d matches'):format(#marks)
  if multiline > 0 then
    msg = msg .. (', %d multi-line skipped'):format(multiline)
  end
  vim.api.nvim_echo({ { msg } }, true, {})
end

-- Whether a match of pat still starts at pos (0-based row and column), asked
-- the way arming found it: searchpos from that position, limited to its line.
-- A buffer search sees the whole line, so foo\zsbar and lookbehinds match at
-- their column - matchstrpos from the stored column cut the context off, every
-- other target counted as dead, and the multi-edit edited nothing - and it
-- applies smartcase as arming did. One search per target: walking the line's
-- matches from column 0 grew with the square of the matches before it.
local function dr_multi_target_alive(pos, pat)
  if pos[1] >= vim.api.nvim_buf_line_count(0) then return false end
  local save = vim.fn.winsaveview()
  local ok, s = pcall(function()
    vim.api.nvim_win_set_cursor(0, { pos[1] + 1, pos[2] })
    return vim.fn.searchpos(pat, 'cnW', pos[1] + 1)
  end)
  vim.fn.winrestview(save)
  return ok and s[1] == pos[1] + 1 and s[2] - 1 == pos[2]
end

-- Runs inside the capture schedule, right after the armed change has been
-- recorded. origin is the insertion point of the user's own edit (in the
-- pre-edit coordinates the arm-time spans are stored in).
dr_finish_multi = function(change, multi, origin)
  dr.replaying = true
  local own_span = nil
  for _, m in ipairs(multi.marks) do
    if m.row == origin[1] and origin[2] >= m.col and origin[2] <= m.endcol then
      own_span = m.id
      break
    end
  end
  local ok, err = pcall(function()
  for _, m in ipairs(multi.marks) do
    -- A span-targeted change skips its own match. A change inserted elsewhere
    -- on the line already edited that line, possibly across several matches.
    local own_line = own_span == nil and m.row == origin[1]
    if m.id ~= own_span and not own_line then
      local pos = vim.api.nvim_buf_get_extmark_by_id(0, dr_multi_ns, m.id, {})
      if #pos > 0 and dr_multi_target_alive(pos, multi.pat) then
        vim.api.nvim_win_set_cursor(0, { pos[1] + 1, pos[2] })
        vim.fn.feedkeys(change.keys, 'x')
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
  end)
  -- Same latch as dr_replay: replaying must come back down whatever happened.
  dr.replaying = false
  vim.api.nvim_buf_clear_namespace(multi.buf, dr_multi_ns, 0, -1)
  if not ok then vim.notify('VSNeo: multi-edit failed: ' .. tostring(err), vim.log.levels.WARN) end
end

-- The extension checks this after nvim_exec_lua: bump it together with
-- Nvim/NvimProtocol.cs when the rpcnotify/rpcrequest contract changes.
return 1
