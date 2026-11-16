local function folds_equal(list)
  if #list ~= #agreed_folds then return false end
  for i = 1, #list do
    if list[i] ~= agreed_folds[i] then return false end
  end
  return true
end

local function expected_foldlevel(line, list)
  local level = 0
  for i = 1, #list, 3 do
    if list[i] <= line and list[i + 1] >= line then level = level + 1 end
  end
  return level
end

-- Whether nvim still has the agreed fold boundaries. An agreed list can
-- outlive the folds themselves: a whole-buffer replacement (a re-prime, a
-- reload) drops nvim's manual folds with the lines, and an identical push
-- afterwards used to be skipped as "nothing changed" - Visual Studio with
-- outlining, nvim with no folds, until the regions happened to change.
local function folds_exist(list)
  for i = 1, #list, 3 do
    local s, e = list[i], list[i + 1]
    for _, line in ipairs({ s - 1, s, e, e + 1 }) do
      if line >= 1 and line <= vim.api.nvim_buf_line_count(0)
          and vim.fn.foldlevel(line) ~= expected_foldlevel(line, list) then
        return false
      end
    end
  end
  return true
end

-- Whether the region starting at s is closed, as far as nvim can say.
-- foldclosed(s) names the OUTERMOST closed fold containing s: -1 when none,
-- s when this region is the closed one, an earlier line when an ancestor
-- is closed and this region is merely hidden inside it - and then its own
-- state is unobservable, so the caller's last known state stands. Reading
-- "~= -1" as closed was what reported every region inside a collapsed
-- namespace as collapsed, and Visual Studio obliged - and expanding the
-- namespace again showed every block in it collapsed.
-- The fold's last line for a region ending at e. Visual Studio's regions
-- start and end mid-line, so a block ending in '}' and the next one starting
-- on that same line ('} else {', '} catch {', '}).Then(() => {') both claim
-- it. nvim's folds are whole lines and cannot overlap that way: the two
-- folds tangled, closing one closed both, and j/k could not get past. The
-- earlier region stops a line short instead - which is also what Visual
-- Studio shows, since a collapsed 'if { ... }' leaves '} else {' visible.
-- starts maps a start line to the largest end of the regions starting there.
local function fit_end(s, e, starts)
  local later = starts[e]
  if later ~= nil and later > e and e - 1 >= s then return e - 1 end
  return e
end

local function starts_of(list)
  local starts = {}
  for i = 1, #list, 3 do
    local s, e = list[i], list[i + 1]
    if starts[s] == nil or e > starts[s] then starts[s] = e end
  end
  return starts
end

-- The line a fitted region gave up: region start -> its original last line.
-- In Visual Studio that line is part of the collapsed line while the region
-- is collapsed - the collapse runs into its middle and the rest is drawn
-- after the '...'. nvim shows it as a line of its own, so a j from the
-- header landed there, Visual Studio found the caret inside collapsed text
-- and snapped it back to the header: j could never pass. While the region
-- is closed, the cursor is not allowed to rest on that line (see push).
local shared_line = {}

-- In place: every region's end fitted against the starts of the others.
local function fit_ends(list)
  local starts = starts_of(list)
  shared_line = {}
  for i = 1, #list, 3 do
    local fitted = fit_end(list[i], list[i + 1], starts)
    if fitted ~= list[i + 1] then shared_line[list[i]] = list[i + 1] end
    list[i + 1] = fitted
  end
  return list
end

-- The closed region whose shared line `line` is, or nil.
local function closed_region_sharing(line)
  for s, raw_end in pairs(shared_line) do
    if raw_end == line and vim.fn.foldclosed(s) == s then return s end
  end
  return nil
end

-- Called with the cursor's previous line before a state push. Moving down
-- onto a collapsed region's shared line goes on to the line after it;
-- moving up onto it goes to the region's header, where Visual Studio's
-- caret sits. Visual Studio never sees the shared line as a cursor line.
local pushed_line = 0
local function step_off_shared_line()
  local cur = vim.api.nvim_win_get_cursor(0)
  local line = cur[1]
  local s = closed_region_sharing(line)
  if s == nil then pushed_line = line return end
  local target
  if line > pushed_line and line < vim.api.nvim_buf_line_count(0) then
    target = line + 1
  else
    target = s
  end
  pcall(vim.api.nvim_win_set_cursor, 0, { target, cur[2] })
  pushed_line = target
end

local function region_state(s, known)
  local fc = vim.fn.foldclosed(s)
  if fc == -1 then return false end
  if fc == s then return true end
  return known
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
-- Diagnostics into %TEMP%\vsneo.log ("nvim: ..."): for the fold state that
-- changes only live, never headless. The last keys nvim received ride
-- along, since a key is the usual reason a fold opened or closed.
local diag_keys = {}
vim.on_key(function(_, typed)
  local k = typed ~= nil and typed ~= '' and typed or nil
  if not k then return end
  diag_keys[#diag_keys + 1] = vim.fn.keytrans(k)
  if #diag_keys > 12 then table.remove(diag_keys, 1) end
end)

local function diag(msg)
  pcall(vim.rpcnotify, chan, 'vsneo_log', msg .. '  [mode ' .. vim.api.nvim_get_mode().mode
    .. ', cursor ' .. vim.fn.line('.') .. ', keys ' .. table.concat(diag_keys, '') .. ']')
end


local function detect_fold_changes()
  if #agreed_folds == 0 then return end
  if vim.b.changedtick ~= agreed_tick then return end
  local actual = {}
  local kept = {}
  local changed = false
  for i = 1, #agreed_folds, 3 do
    local s, e, closed = agreed_folds[i], agreed_folds[i + 1], agreed_folds[i + 2]
    local exists = vim.fn.foldlevel(s) > 0
    -- A region hidden inside a closed ancestor keeps its agreed state: the
    -- user cannot have changed what they cannot see, and reporting it
    -- closed collapsed every block inside a collapsed namespace.
    local is_closed = exists and region_state(s, closed)
    if exists then
      actual[#actual + 1] = s
      actual[#actual + 1] = e
      actual[#actual + 1] = is_closed
      kept[#kept + 1] = s
      kept[#kept + 1] = e
      kept[#kept + 1] = is_closed
    end
    if not exists or is_closed ~= closed then
      changed = true
      diag(('fold %d-%d %s (agreed %s, foldclosed %d, foldlevel %d)'):format(
        s, e, not exists and 'vanished' or (is_closed and 'closed' or 'opened'),
        tostring(closed), vim.fn.foldclosed(s), vim.fn.foldlevel(s)))
    end
  end
  if changed then
    agreed_folds = kept
    agreed_tick = vim.b.changedtick
    vim.rpcnotify(chan, 'vsneo_folds_changed', actual)
  end
end

------------------------------------------------------------------
-- Region text objects and motions over Visual Studio's outlining regions
--
-- The regions mirrored in as folds are the language service's: methods,
-- classes, namespaces, blocks, #regions. That makes them a free, exact,
-- language-agnostic structure to move over - what nvim-treesitter-
-- textobjects gives Neovim users, without a grammar. The innermost region
-- containing the cursor is "a region"; a count asks for an outer one
-- (2ar: the region around that). ir is the region without its first and
-- last line (a method without its signature and closing brace). ]r / [r go
-- to the next / previous region start, ]R / [R to a region end. Linewise,
-- like the regions themselves.
------------------------------------------------------------------

-- Empty while the buffer has moved on from the last agreed boundaries - the
-- gate detect_fold_changes uses. An edit shifts every region below it, but
-- the agreed list keeps the old lines until Visual Studio resends it (400 ms
-- after editing pauses), and dar right after an o or dd took the lines where
-- the method used to be. ponytail: refuses for that window rather than
-- tracking the shift; extmarks on the agreed boundaries would close it.
local function regions_sorted()
  local out = {}
  if vim.b.changedtick ~= agreed_tick then
    if #agreed_folds > 0 then
      vim.api.nvim_echo({ { 'VSNeo: regions are being re-read after the edit - try again in a moment',
        'WarningMsg' } }, false, {})
    end
    return out
  end
  -- Visual Studio's own end, not the fitted one: a region ending on the next
  -- one's header line (`} else {`) is a line shorter as a fold, and ir left
  -- its last body line behind.
  for i = 1, #agreed_folds, 3 do
    local s = agreed_folds[i]
    out[#out + 1] = { s = s, e = shared_line[s] or agreed_folds[i + 1] }
  end
  table.sort(out, function(a, b)
    if a.s ~= b.s then return a.s < b.s end
    return a.e > b.e
  end)
  return out
end

-- The depth-th region containing line, innermost first.
local function region_around(line, depth)
  local containing = {}
  for _, r in ipairs(regions_sorted()) do
    if r.s <= line and line <= r.e then containing[#containing + 1] = r end
  end
  table.sort(containing, function(a, b) return (a.e - a.s) < (b.e - b.s) end)
  return containing[depth]
end

local function select_region(inner)
  local line = vim.api.nvim_win_get_cursor(0)[1]
  local r = region_around(line, vim.v.count1)
  if not r then return end
  local s, e = r.s, r.e
  if inner then
    if e - s < 2 then return end   -- nothing between the header and the end
    s, e = s + 1, e - 1
  end
  -- The text-object technique: in charwise or blockwise visual, V switches
  -- the kind to linewise; in operator-pending it starts a linewise visual
  -- the operator then applies to. Then the anchor is moved with o, never
  -- an Escape - leaving and re-entering visual from inside a mapping does
  -- not survive feedkeys.
  if vim.api.nvim_get_mode().mode ~= 'V' then vim.cmd('normal! V') end
  vim.api.nvim_win_set_cursor(0, { s, 0 })
  vim.cmd('normal! o')
  vim.api.nvim_win_set_cursor(0, { e, 0 })
end

local function region_motion(forward, to_end)
  local line = vim.api.nvim_win_get_cursor(0)[1]
  local count = vim.v.count1
  local targets = {}
  for _, r in ipairs(regions_sorted()) do
    targets[#targets + 1] = to_end and r.e or r.s
  end
  table.sort(targets)
  local picked, seen = nil, 0
  if forward then
    for _, l in ipairs(targets) do
      if l > line then
        seen = seen + 1
        if seen == count then picked = l break end
      end
    end
  else
    for i = #targets, 1, -1 do
      local l = targets[i]
      if l < line then
        seen = seen + 1
        if seen == count then picked = l break end
      end
    end
  end
  if not picked then return end
  -- Linewise under an operator (d]r takes whole lines, as ]] does in
  -- practice), and a jumplist entry so '' comes back.
  if vim.api.nvim_get_mode().mode == 'no' then vim.cmd('normal! V') end
  vim.cmd("normal! m'")
  vim.api.nvim_win_set_cursor(0, { picked, 0 })
  vim.cmd('normal! ^')
end

vim.keymap.set({ 'x', 'o' }, 'ar', function() select_region(false) end,
  { desc = 'a region (VS outlining)' })
vim.keymap.set({ 'x', 'o' }, 'ir', function() select_region(true) end,
  { desc = 'inner region (VS outlining)' })
vim.keymap.set({ 'n', 'x', 'o' }, ']r', function() region_motion(true, false) end,
  { desc = 'next region start' })
vim.keymap.set({ 'n', 'x', 'o' }, '[r', function() region_motion(false, false) end,
  { desc = 'previous region start' })
vim.keymap.set({ 'n', 'x', 'o' }, ']R', function() region_motion(true, true) end,
  { desc = 'next region end' })
vim.keymap.set({ 'n', 'x', 'o' }, '[R', function() region_motion(false, true) end,
  { desc = 'previous region end' })

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
local pending_folds = nil   -- { buf = <bufnr>, list = { s, e, closed, ... }, tick = <changedtick> }

-- tick: the changedtick the list's boundaries were true at - now, unless the
-- list waited out an insert session (pending_folds). Stamped with the tick at
-- apply time, a deferred list read as current after edits typed since, and
-- dar right after <Esc> took lines those edits had shifted.
local function apply_folds(list, tick)
  folds_dirty = false
  local view = vim.fn.winsaveview()
  vim.cmd('normal! zE')
  -- Created open: ':fold' makes a closed fold, and a region created while
  -- its parent is closed does not nest under it. Every region is opened
  -- right after creation (ancestors included - nothing is closed yet).
  -- A list deferred through pending_folds can outlive lines deleted during
  -- insert: clamp to the buffer, and pcall each region so one bad range
  -- (E16) cannot leave every fold gone after the zE above.
  local last = vim.api.nvim_buf_line_count(0)
  for i = 1, #list, 3 do
    if list[i + 1] > last then list[i + 1] = last end
    local s, e = list[i], list[i + 1]
    if s <= last and e >= s then
      pcall(vim.cmd, s .. ',' .. e .. 'fold')
      pcall(vim.cmd, s .. 'foldopen!')
    end
  end
  -- Then the closed ones closed innermost first: a region is the deepest
  -- open fold containing its own first line as long as its descendants are
  -- already closed or do not start there, so one level of 'foldclose' at
  -- that line closes exactly it - never an ancestor.
  local closed = {}
  for i = 1, #list, 3 do
    if list[i + 2] and list[i + 1] >= list[i] and list[i] <= last then closed[#closed + 1] = i end
  end
  table.sort(closed, function(a, b) return (list[a + 1] - list[a]) < (list[b + 1] - list[b]) end)
  for _, i in ipairs(closed) do
    if vim.fn.foldclosed(list[i]) == -1 then vim.cmd(list[i] .. 'foldclose') end
  end
  vim.fn.winrestview(view)
  -- nvim cannot close everything Visual Studio can (a one-line fold never
  -- closes: 'foldminlines'), so the agreed copy records the state nvim
  -- actually reached, not the requested one - otherwise every push would
  -- detect the unreachable closed state as a change and ping VS forever. A
  -- region hidden inside a closed ancestor keeps the requested state.
  for i = 1, #list, 3 do
    list[i + 2] = region_state(list[i], list[i + 2])
  end
  agreed_folds = list
  agreed_tick = tick or vim.b.changedtick
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
    apply_folds(pending.list, pending.tick)
  end,
})

-- True from the n:no transition until the operator-pending mode is left.
-- An autocmd that runs while an operator waits for its motion reads the mode
-- as plain 'n' - nvim_get_mode() and mode(1) both - so a push from CursorMoved
-- (the viewport sync moves the cursor, and the showcmd margin appearing after
-- `c` resizes the view just such a moment later) told the extension that
-- normal mode was back while nvim still held the operator. A click then took
-- the plain-click path, moved the operator's end, and `k` ran it over a range
-- nobody typed: the line disappeared. Created before the push autocmds, so
-- the flag is current when a ModeChanged push reads it.
local op_pending = false
vim.api.nvim_create_autocmd('ModeChanged', {
  group = group,
  pattern = '*:*',
  callback = function()
    op_pending = vim.v.event.new_mode:sub(1, 2) == 'no'
  end,
})

local function push()
  detect_fold_changes()
  if vim.api.nvim_get_mode().mode:sub(1, 1) == 'n' then step_off_shared_line() end
  local ok, pos = pcall(vim.api.nvim_win_get_cursor, 0)
  if not ok then return end

  local syn = synthetic_cursor
  synthetic_cursor = false

  local m = vim.api.nvim_get_mode().mode
  if m == 'n' and op_pending then m = 'no' end
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

-- Every <Esc> ends with a push, whether or not it changed anything. The
-- extension's mode cache only moves when a push arrives, and a push only
-- comes from a mode change or a cursor move - so a cache that ever read
-- Insert while nvim sat in normal could not be repaired by Escape: nvim had
-- nothing to leave, fired no ModeChanged, and every key after it kept
-- passing through as typed text ('.' inserting a dot, Escape "doing
-- nothing"). Scheduled, so it reports the state after the key was
-- processed; the extension drops a push that changes nothing, so the
-- normal case costs one notification per Escape.
local esc_key = vim.api.nvim_replace_termcodes('<Esc>', true, false, true)
vim.on_key(function(key)
  if key == esc_key then vim.schedule(push) end
end, vim.api.nvim_create_namespace('vsneo_esc_push'))

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
