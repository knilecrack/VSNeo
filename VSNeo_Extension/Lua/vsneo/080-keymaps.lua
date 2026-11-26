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

-- Insert-mode mappings the user declared on named keys (<Left>, <C-x>).
-- Only a single <...> token can ever be claimed: a printable lhs arrives in
-- Visual Studio as text, not as a key, so no interception point ever sees
-- it - that is why 'imap a A' can never work here, and why nvim's defaults
-- are filtered out (claiming a default <C-W> or <Tab> would steal the key
-- from Visual Studio for every user, mapping or not). The rhs deliberately
-- does not cross the wire: the extension feeds the lhs back through
-- nvim_input and nvim runs the mapping itself, so string, Lua-callback and
-- expr rhs all work untouched.
local function collect_imaps()
  local items = {}
  local seen = {}
  local function add(maps, buffer_local)
    for _, m in ipairs(maps) do
      local lhs = m.lhs
      if not seen[lhs]
        and lhs:match('^<[^>]+>$')
        and not lhs:find('<Plug>', 1, true) then
        -- Buffer-local mappings are always the user's (nvim's defaults are
        -- never buffer-local); a global counts when it is new or its rhs
        -- or callback differs from the shipped default.
        local d = default_imaps[lhs]
        if buffer_local or d == nil or d.rhs ~= (m.rhs or '') or d.callback ~= m.callback then
          seen[lhs] = true
          table.insert(items, lhs)
        end
      end
    end
  end
  -- Buffer-local first, same shadowing rule as collect_keymaps.
  add(vim.api.nvim_buf_get_keymap(0, 'i'), true)
  add(vim.api.nvim_get_keymap('i'), false)
  return items
end

local function send_keymaps()
  for _, mode in ipairs({ 'n', 'x' }) do
    vim.rpcnotify(chan, 'vsneo_keymaps', mode, collect_keymaps(mode))
  end
  vim.rpcnotify(chan, 'vsneo_imaps', collect_imaps())
end

local keymaps_timer = nil
vim.api.nvim_create_autocmd('SourcePost', {
  group = group,
  callback = function()
    -- close as well as stop: defer_fn only closes a timer that fires, so a
    -- restarted one leaked a uv handle per SourcePost burst.
    if keymaps_timer and not keymaps_timer:is_closing() then
      keymaps_timer:stop()
      keymaps_timer:close()
    end
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
--   * the inserted text is read back off the buffer: a bracket opened at
--     the insertion point follows whatever the session inserts, whether
--     it arrived as keystrokes or over the API (see ins_begin below).
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
--
-- "Moved on" means a change nvim made itself. A write the mirror makes for
-- Visual Studio is not a Vim change and takes nothing from redo: u and
-- <C-r> run against Visual Studio's history and come back as mirror writes,
-- and so do a refactoring, a format, a drift repair. The recorded change
-- rides its tick across those (mirror_wrote); it used to go stale instead,
-- and change, u, '.' - the first thing anyone tries - fell back to native
-- redo and deleted the next target without inserting anything.
------------------------------------------------------------------

local dr = {
  pending = {},       -- keys seen in normal/operator-pending, oldest first
  op_start = nil,     -- index into pending of the in-flight operator key
  visual = false,     -- the in-flight change came from a visual selection
  entry = nil,        -- the open insert session (ins_begin)
  candidate = nil,    -- {keys, visual} captured at insert entry
  change = nil,       -- last insert-change: {buf, tick, keys, text}, {visual=true},
                      -- or {rejected=why, buf, tick} when its text was not captured
  replay_entry = nil, -- {row0, col0} captured during a replay's insert flap
  replaying = false,  -- fed keys and their events must not be tracked
}

-- Multi-edit state, filled in by the section further down. Forward
-- declared: the ModeChanged autocmd below consumes the arming when the
-- change it was waiting for is captured.
local dr_multi = nil         -- armed: {buf, marks={...}}; nil when idle
local dr_finish_multi = nil  -- function assigned in the multi-edit section

