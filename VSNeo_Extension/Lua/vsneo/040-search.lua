-- The native s is cl's synonym, so this mapping costs nothing; unmap or
-- rebind it from ~/.vsneorc if it is in the way.
-- Labels and macros do not mix (flash.nvim's rule): replaying, the label
-- prompt ate the macro's next key as a pick, so fxdw on a line with several
-- x landed elsewhere and w ran as a motion. Recording falls back too, so
-- the register holds the native keys (fx, not fx + label) that replay
-- reproduces. Fed with 'i' so the native key reads its target from the
-- register's own typeahead.
local function in_macro()
  return vim.fn.reg_executing() ~= '' or vim.fn.reg_recording() ~= ''
end
local function feed_native(key)
  vim.api.nvim_feedkeys((vim.v.count > 0 and tostring(vim.v.count) or '') .. key, 'ni', false)
end

vim.keymap.set('n', 's', function()
  if in_macro() then feed_native('s') return end
  _G.vsneo.jump()
end, { silent = true, desc = 'VSNeo: jump to a visible match' })

--- flash-style f/F/t/T: read the target char, and when the line holds
--- several matches in that direction, label them and let the label key
--- pick one. Zero or one match never shows labels, and the landing is
--- always the native motion (fed with a count), so ; and , keep working.
local function jump_char(key)
  if in_macro() then feed_native(key) return end
  -- Read before getcharstr: the count belongs to this motion. It used to be
  -- dropped by every feedkeys below, so 3fx behaved like fx.
  local count1 = vim.v.count1
  local ok, ch = pcall(vim.fn.getcharstr)
  -- Anything that is not one printable byte (Escape, arrows, multibyte)
  -- cannot be labeled: hand the whole thing back to the native motion.
  if not ok or #ch ~= 1 or ch:byte() < 32 then
    if ok then vim.api.nvim_feedkeys(tostring(count1) .. key .. ch, 'n', false) end
    return
  end
  -- A counted motion names its target already; labels would only get in
  -- the way of 3fx.
  if count1 > 1 then
    vim.api.nvim_feedkeys(tostring(count1) .. key .. ch, 'n', false)
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

-- match() and matchstrpos() honour 'ignorecase' but never 'smartcase' - that
-- is a rule of the search commands, not of the regex engine - so with both
-- set, /Foo matched foo as well. Vim's test: an uppercase letter in the
-- pattern, not counting the ones after a backslash (\S, \W).
local function with_smartcase(pattern)
  if vim.o.ignorecase and vim.o.smartcase
     and not pattern:find('\\[cC]')
     and pattern:gsub('\\.', ''):find('%u') then
    return '\\C' .. pattern
  end
  return pattern
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

  local scan_pattern = with_smartcase(pattern)

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
      -- byte offset, and the count argument (1) is what makes anchors hold
      -- against the whole line: without it Vim treats the string as
      -- starting at offset, so ^ and \< matched at every restart - /^x on
      -- xxx highlighted all three.
      local m = vim.fn.matchstrpos(line, scan_pattern, offset, 1)
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

  -- The scan above covers the window plus a margin, but the [n/N] chip is
  -- about the whole buffer: it used to say [1/240] in a 5000-line file and
  -- drift once scrolled. searchcount walks the buffer natively (bounded by
  -- the timeout, so a huge file reports a floor rather than stalling the
  -- main loop) and, anchored at the scan's first line, says how many
  -- matches precede it; the extension adds the index within the scanned
  -- list. Sent before the matches, so the count is in place when their
  -- arrival triggers the redraw.
  local before, total, incomplete = 0, #matches, 0
  local okc, sc = pcall(vim.fn.searchcount, {
    recompute = 1, maxcount = 0, timeout = 50, pattern = pattern, pos = { first, 1, 0 },
  })
  if okc and type(sc) == 'table' and sc.total then
    before = (sc.current or 0) - (sc.exact_match or 0)
    total = sc.total
    incomplete = sc.incomplete or 0
  end
  vim.rpcnotify(chan, 'vsneo_search_count', before, total, incomplete)
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

-- :nohlsearch changes v:hlsearch without a motion, edit or event of its own,
-- and it arrives every way there is: typed, from a <Cmd> mapping (kickstart's
-- <Esc>, nvim's default <C-L>), from :normal. A CmdlineLeave ':' hook saw only
-- the typed one, and it shared the 'leave' timer with the / and ? scan, so a
-- : command within 50 ms of a search replaced that forced scan with one that
-- returned early. So every key schedules a look at v:hlsearch once it has
-- been processed, and a change rescans - off, that is the empty list that
-- clears Visual Studio's highlights. The search register is watched the same
-- way: :let @/ = ... (a "* without moving" mapping) changes the pattern with
-- no motion either, and the ':' hook was its only rescan.
local hlsearch_seen, pattern_seen = vim.v.hlsearch, vim.fn.getreg('/')
vim.on_key(function()
  schedule_search_scan('hlsearch', 0, function()
    local hl, pat = vim.v.hlsearch, vim.fn.getreg('/')
    if hl == hlsearch_seen and pat == pattern_seen then return end
    hlsearch_seen, pattern_seen = hl, pat
    send_search_matches(false)
  end)
end, vim.api.nvim_create_namespace('vsneo_hlsearch'))

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
-- Marks on the scrollbar (ScrollbarMarkTagger.cs)
--
-- Visual Studio draws a tick on its scrollbar for every mark in a document;
-- nvim owns the marks, so it reports them: vsneo_marks is [path, list] with
-- one [0-based line, name] per mark - a-z for the current buffer, and A-Z
-- that point into it. Setting a mark moves nothing and fires no event, so the
-- list is re-checked shortly after any key (and after edits, which shift
-- marks, and on buffer switches); it is sent only when it changed, so the
-- ordinary keystroke costs one short getmarklist().
------------------------------------------------------------------

local sent_marks = {}   -- path -> signature of the list last sent

local function send_marks()
  local buf = vim.api.nvim_get_current_buf()
  local path = vim.api.nvim_buf_get_name(buf)
  if path == '' then return end

  local list = {}
  for _, m in ipairs(vim.fn.getmarklist(buf)) do
    local name = m.mark:sub(2)
    if name:match('^%l$') then list[#list + 1] = { m.pos[2] - 1, name } end
  end
  for _, m in ipairs(vim.fn.getmarklist()) do
    local name = m.mark:sub(2)
    if name:match('^%u$') and m.pos[1] == buf then list[#list + 1] = { m.pos[2] - 1, name } end
  end
  table.sort(list, function(a, b) return a[1] < b[1] or (a[1] == b[1] and a[2] < b[2]) end)

  local parts = {}
  for i, e in ipairs(list) do parts[i] = e[1] .. ':' .. e[2] end
  local signature = table.concat(parts, ',')
  if sent_marks[path] == signature then return end
  sent_marks[path] = signature

  vim.rpcnotify(chan, 'vsneo_marks', path, list)
end

local marks_ns = vim.api.nvim_create_namespace('vsneo_marks')
vim.on_key(function()
  schedule_search_scan('marks', 40, send_marks)
end, marks_ns)

vim.api.nvim_create_autocmd({ 'BufEnter', 'TextChanged' }, {
  group = group,
  callback = function()
    schedule_search_scan('marks', 40, send_marks)
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
local vs_command_names = {}

