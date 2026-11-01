-- vsneo.note_viewport(topline, height, caretline, caretcol, caret_visible):
-- the cursor never clamps when Visual Studio says the caret is on screen
-- (folds compress the view, so topline+height arithmetic cannot decide),
-- and clamps to the window edge only when the caret is genuinely off screen.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/viewport.lua' })

-- The clamp reads the window's real extent (line('w$')), which production
-- sizes with nvim_ui_try_resize. A headless screen cannot be resized, so the
-- clamp cases pass the window's actual height, as the extension would.
local H = vim.api.nvim_win_get_height(0)

-- in-window: topline applies, cursor takes the caret's exact position
vim.api.nvim_win_set_cursor(0, { 10, 0 })
vsneo.note_viewport(8, 40, 10, 0, true)
t.eq(vim.fn.line('w0'), 8, 'winrestview should put the topline at 8')
t.eq(vim.api.nvim_win_get_cursor(0)[1], 10, 'cursor should sit on the caret line')

-- vertical padding survives synchronization. A Visual Studio topline that
-- puts the caret inside the padding keeps the window where Visual Studio
-- put it and moves nvim's cursor instead, as <C-e> does in Vim: a corrected
-- topline was one Visual Studio never sent, and it scrolled back
vim.wo.scrolloff = 3
vim.cmd('doautocmd SourcePost')
t.eq(vim.wo.scrolloff, 3, 'invariant reassertion must preserve scrolloff')
local scrolloff = t.report(h, 'vsneo_scrolloff')
t.expect(scrolloff ~= nil, 'scrolloff should be pushed to the extension')
t.eq(scrolloff[2], 3, 'the effective scrolloff should be pushed')
t.clear(h)
vsneo.note_viewport(8, H, 10, 0, true)
t.eq(vim.fn.line('w0'), 8, 'note_viewport must keep the topline Visual Studio sent')
t.eq(vim.api.nvim_win_get_cursor(0)[1], 11, 'the cursor moves out of the top padding')
-- a real move, not synthetic: the caret is on screen and must follow, or it
-- stays drawn on a line nvim has left and dd deletes the other one (the push
-- itself comes from CursorMoved, which a -l script never reaches)
local padded = t.report(h, 'vsneo_state')
t.expect(padded == nil or padded[9] ~= true, 'a padding move of a visible caret must not be synthetic')
t.eq(vim.wo.scrolloff, 3, 'note_viewport must not clear scrolloff')
-- the bottom padding, and a caret below the window clamps to its edge
vsneo.note_viewport(8, H, 8 + H - 2, 0, true)
t.eq(vim.fn.line('w0'), 8, 'bottom padding: the window stays')
t.eq(vim.api.nvim_win_get_cursor(0)[1], 8 + H - 1 - 3, 'the cursor moves out of the bottom padding')
vsneo.note_viewport(8, H, 90, 0, false)
t.eq(vim.fn.line('w0'), 8, 'off screen with padding: the window stays')
t.eq(vim.api.nvim_win_get_cursor(0)[1], 8 + H - 1 - 3, 'off screen with padding: clamp to the band edge')
-- the first lines of the buffer have no padding to keep
vsneo.note_viewport(1, H, 1, 0, true)
t.eq(vim.api.nvim_win_get_cursor(0)[1], 1, 'no top padding at the start of the buffer')
-- nor the last ones, when a closed fold runs to the end of the buffer and the
-- window shows it: rows_from answers that fold by its start line
local F = H - 3
vim.cmd(F .. ',$fold')
vsneo.note_viewport(1, H, F - 1, 0, true)
t.eq(vim.api.nvim_win_get_cursor(0)[1], F - 1, 'no bottom padding when a fold reaches the end')
vim.cmd('normal! zE')
-- (insert mode, where padding never moves the caret: the child rig below)
vsneo.note_viewport(1, H, 10, 0, true)
vsneo.set_topline(9)   -- the cursor on 10 would sit in the top padding
t.eq(vim.fn.line('w0'), 1, 'set_topline must refuse a padded topline correction')
t.eq(vim.wo.scrolloff, 3, 'set_topline must not clear scrolloff')
vim.wo.scrolloff = 0

-- caret visible but outside topline+height (a fold compresses the view):
-- NO clamp - the cursor goes to the caret line
vsneo.note_viewport(8, 40, 70, 0, true)
t.eq(vim.api.nvim_win_get_cursor(0)[1], 70,
  'visible caret outside topline+height must not be clamped')

-- caret genuinely off screen: clamp to the window edge nearest the caret,
-- and the push carries the synthetic flag
t.clear(h)
vsneo.note_viewport(8, H, 70, 0, false)
t.eq(vim.api.nvim_win_get_cursor(0)[1], 8 + H - 1,
  'off-screen caret should clamp to botline (topline + height - 1)')
local state = t.report(h, 'vsneo_state')
t.expect(state ~= nil, 'clamped cursor should push state')
t.eq(state[9], true, 'clamped cursor push should be flagged synthetic')
t.eq(state[3], 8 + H - 2, 'state push should carry the clamped line, 0-based')

-- clamp upwards: caret above the window
vsneo.note_viewport(20, H, 3, 0, false)
t.eq(vim.api.nvim_win_get_cursor(0)[1], 20,
  'off-screen caret above should clamp to the topline')

-- a closed fold on screen is one row: the window reaches further down, and
-- the clamp lands on the real bottom row, not topline + height - 1
vim.cmd('20,39fold')
t.eq(vim.fn.foldclosed(25), 20, 'test setup: fold 20-39 should be closed')
vsneo.note_viewport(8, H, 90, 0, false)
t.eq(vim.api.nvim_win_get_cursor(0)[1], 8 + H - 1 + 19,
  'off-screen caret should clamp to the real botline with a fold on screen')

-- clamping upwards onto a closed fold lands on the fold's first line
vsneo.note_viewport(25, H, 3, 0, false)
t.eq(vim.api.nvim_win_get_cursor(0)[1], 20,
  'a clamp inside a closed fold should land on the fold start')
vim.cmd('normal! zE')

-- set_topline: nvim's window follows an amplified jump without moving the
-- cursor, and refuses a topline that would leave the cursor off screen
vim.api.nvim_win_set_cursor(0, { 30, 0 })
vsneo.note_viewport(20, H, 30, 0, true)
vsneo.set_topline(25)
t.eq(vim.fn.line('w0'), 25, 'set_topline should move the window')
t.eq(vim.api.nvim_win_get_cursor(0)[1], 30, 'set_topline must not move the cursor')
vsneo.set_topline(60)
t.eq(vim.fn.line('w0'), 25, 'a topline that would hide the cursor is refused')
t.eq(vim.api.nvim_win_get_cursor(0)[1], 30, 'a refused topline leaves the cursor alone')

-- a stale update is dropped: it was captured while nvim's cursor was on
-- line 44, but keys moved it since (gg ran while the request waited out the
-- pending g) - applying it would put the cursor and window back
vim.api.nvim_win_set_cursor(0, { 1, 0 })
vsneo.note_viewport(1, H, 1, 0, true)
local ok = vsneo.note_viewport(23, H, 44, 0, true, 44, 0)
t.eq(ok, false, 'a note_viewport captured before the cursor moved should report stale')
t.eq(vim.api.nvim_win_get_cursor(0)[1], 1, 'a stale note_viewport must not move the cursor')
t.eq(vim.fn.line('w0'), 1, 'a stale note_viewport must not move the window')

-- the same update, with nvim's cursor where the extension last saw it, applies
ok = vsneo.note_viewport(5, H, 10, 0, true, 1, 0)
t.eq(ok, true, 'a current note_viewport should report applied')
t.eq(vim.api.nvim_win_get_cursor(0)[1], 10, 'a current note_viewport rejoins the caret')
t.eq(vim.fn.line('w0'), 5, 'a current note_viewport moves the window')

-- zz scrolls without moving the cursor, so the cursor check cannot see it:
-- an update captured at topline 5 (known_top) and queued behind the first z
-- must not put the window back after zz
vim.cmd('normal! zz')
local after_zz = vim.fn.line('w0')
t.expect(after_zz ~= 5, 'test setup: zz should have scrolled')
ok = vsneo.note_viewport(5, H, 10, 0, true, 10, 0, 5)
t.eq(ok, false, 'a note_viewport captured before zz should report stale')
t.eq(vim.fn.line('w0'), after_zz, 'a stale note_viewport must not undo zz')

-- dropped once: the resend carries the same known_top if no report arrived,
-- and must apply rather than refuse forever
ok = vsneo.note_viewport(5, H, 10, 0, true, 10, 0, 5)
t.eq(ok, true, 'the resend after a topline drop should apply')

-- the window where Visual Studio itself put it passes, before its report
-- reaches the extension (known_top still the older line)
ok = vsneo.note_viewport(7, H, 10, 0, true, 10, 0, 1)
t.eq(ok, true, 'a topline Visual Studio set should not count as nvim scrolling')

-- visual mode: the cursor is one end of the selection; never touched
vim.api.nvim_win_set_cursor(0, { 30, 0 })
vim.fn.feedkeys('v', 'nx')
t.eq(vim.api.nvim_get_mode().mode:sub(1, 1), 'v', 'test setup: not in visual mode')
vsneo.note_viewport(1, 40, 90, 0, false)
t.eq(vim.api.nvim_win_get_cursor(0)[1], 30, 'visual-mode note_viewport must not move the cursor')
vim.fn.feedkeys(vim.api.nvim_replace_termcodes('<Esc>', true, false, true), 'nx')

-- The command-line guard (an incsearch match IS the cursor) cannot be tested
-- here: a headless -l script never enters cmdline mode, feedkeys(':')
-- included.

-- Operator-pending and replace modes need the production-style child RPC rig:
-- a single-process -l script cannot keep those modes open across an RPC call.
local companion = vim.fn.fnamemodify('VSNeo_Extension/Lua/vsneo.lua', ':p')
local chan = vim.fn.jobstart({ vim.v.progpath, '--embed', '--headless', '-u', 'NONE', '-i', 'NONE' },
  { rpc = true })
assert(chan > 0, 'could not start child nvim')
local function req(method, ...) return vim.fn.rpcrequest(chan, method, ...) end
local function lua(code, ...) return req('nvim_exec_lua', code, { ... }) end
local function child_eq(actual, expected, msg)
  if actual ~= expected then
    vim.fn.jobstop(chan)
    error(('FAILED: %s (expected %s, got %s)'):format(msg, tostring(expected), tostring(actual)), 2)
  end
end

lua([[
  vim.rpcnotify = function() end
  assert(loadfile(...))(0)
  local lines = {}
  for i = 1, 100 do lines[i] = 'line ' .. i end
  vim.api.nvim_buf_set_lines(0, 0, -1, false, lines)
  vim.api.nvim_win_set_cursor(0, { 50, 0 })
]], companion)

req('nvim_input', 'd')
lua('vim.wait(40)')
child_eq(lua("return vim.api.nvim_get_mode().mode:sub(1, 2)"), 'no',
  'test setup: not in operator-pending mode')
lua('vsneo.note_viewport(1, vim.api.nvim_win_get_height(0), 50, 0, false)')
child_eq(lua('return vim.api.nvim_win_get_cursor(0)[1]'), 50,
  'operator-pending note_viewport must not move the cursor')
req('nvim_input', 'j')
lua('vim.wait(40)')
child_eq(lua('return vim.api.nvim_buf_line_count(0)'), 98,
  'viewport sync must not widen an operator-pending change')
child_eq(lua('return vim.fn.getline(50)'), 'line 52', 'd j should delete only lines 50 and 51')

lua('vim.api.nvim_win_set_cursor(0, { 60, 0 })')
req('nvim_input', 'R')
lua('vim.wait(40)')
child_eq(lua("return vim.api.nvim_get_mode().mode:sub(1, 1)"), 'R', 'test setup: not in replace mode')
child_eq(lua('return vsneo.note_viewport(1, vim.api.nvim_win_get_height(0), 60, 0, false)'), 'deferred',
  'replace mode answers deferred, so the extension resends once it ends')
child_eq(lua('return vim.api.nvim_win_get_cursor(0)[1]'), 60,
  'replace-mode note_viewport must not move the cursor')
req('nvim_input', 'ZZZ<Esc>')
lua('vim.wait(40)')
child_eq(lua('return vim.fn.getline(60)'), 'ZZZe 62', 'replace text must land at the original cursor')

-- insert mode: the caret is Visual Studio's, so a caret inside the padding
-- rejoins nvim's cursor as it is - only normal mode moves it out
lua('vim.wo.scrolloff = 3; vim.api.nvim_win_set_cursor(0, { 30, 0 })')
req('nvim_input', 'i')
lua('vim.wait(40)')
child_eq(lua("return vim.api.nvim_get_mode().mode"), 'i', 'test setup: not in insert mode')
lua('vsneo.note_viewport(29, vim.api.nvim_win_get_height(0), 30, 0, true)')
child_eq(lua('return vim.api.nvim_win_get_cursor(0)[1]'), 30, 'insert mode: no padding move')
req('nvim_input', '<Esc>')

vim.fn.jobstop(chan)
print('viewport_tests: ALL OK')
