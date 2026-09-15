-- vsneo.note_viewport(topline, height, caretline, caretcol, caret_visible):
-- the cursor never clamps when Visual Studio says the caret is on screen
-- (folds compress the view, so topline+height arithmetic cannot decide),
-- and clamps to the window edge only when the caret is genuinely off screen.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/viewport.lua' })

-- in-window: topline applies, cursor takes the caret's exact position
vim.api.nvim_win_set_cursor(0, { 10, 0 })
vsneo.note_viewport(8, 40, 10, 0, true)
t.eq(vim.fn.line('w0'), 8, 'winrestview should put the topline at 8')
t.eq(vim.api.nvim_win_get_cursor(0)[1], 10, 'cursor should sit on the caret line')

-- caret visible but outside topline+height (a fold compresses the view):
-- NO clamp - the cursor goes to the caret line
vsneo.note_viewport(8, 40, 70, 0, true)
t.eq(vim.api.nvim_win_get_cursor(0)[1], 70,
  'visible caret outside topline+height must not be clamped')

-- caret genuinely off screen: clamp to the window edge nearest the caret,
-- and the push carries the synthetic flag
t.clear(h)
vsneo.note_viewport(8, 40, 70, 0, false)
t.eq(vim.api.nvim_win_get_cursor(0)[1], 47,
  'off-screen caret should clamp to botline (topline + height - 1)')
local state = t.report(h, 'vsneo_state')
t.expect(state ~= nil, 'clamped cursor should push state')
t.eq(state[9], true, 'clamped cursor push should be flagged synthetic')
t.eq(state[3], 46, 'state push should carry the clamped line, 0-based')

-- clamp upwards: caret above the window
vsneo.note_viewport(20, 40, 3, 0, false)
t.eq(vim.api.nvim_win_get_cursor(0)[1], 20,
  'off-screen caret above should clamp to the topline')

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

print('viewport_tests: ALL OK')
