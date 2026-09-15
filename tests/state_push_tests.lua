-- The push() contract: one vsneo_state notification carrying
-- [mode, 0-based line, byte column, w0, anchor line, anchor col, ...],
-- the visual anchor when a selection is active, and w0 == -1 ("no scroll
-- information") on pushes fired from inside vsneo.set_cursor.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/state.lua' })

-- a plain cursor move reports mode, 0-based line, byte column, topline
t.clear(h)
vim.api.nvim_win_set_cursor(0, { 42, 3 })
vim.cmd('doautocmd CursorMoved')
local state = t.report(h, 'vsneo_state')
t.expect(state ~= nil, 'no vsneo_state after a cursor move')
t.eq(state[2], 'n', 'mode should be normal')
t.eq(state[3], 41, 'line should be 0-based')
t.eq(state[4], 3, 'column should be the byte offset')
t.eq(state[5], vim.fn.line('w0') - 1, 'w0 should be the 0-based topline')
t.eq(state[6], -1, 'no visual anchor in normal mode')

-- visual mode reports the other end of the selection
t.clear(h)
vim.api.nvim_win_set_cursor(0, { 10, 2 })
vim.fn.feedkeys('v', 'nx')
vim.api.nvim_win_set_cursor(0, { 12, 5 })
vim.cmd('doautocmd CursorMoved')
state = t.report(h, 'vsneo_state')
t.expect(state ~= nil, 'no vsneo_state in visual mode')
t.eq(state[2]:sub(1, 1), 'v', 'mode should be visual')
t.eq(state[6], 9, 'anchor line should be 0-based 9')
t.eq(state[7], 2, 'anchor column should be the byte offset')
vim.fn.feedkeys(vim.api.nvim_replace_termcodes('<Esc>', true, false, true), 'nx')

-- vsneo.set_cursor: the cursor lands on the pushed position
t.clear(h)
vsneo.set_cursor(50, 4)
t.eq(vim.api.nvim_win_get_cursor(0)[1], 50, 'set_cursor should move the cursor')
t.eq(vim.api.nvim_win_get_cursor(0)[2], 4, 'set_cursor should move the column')

-- The w0 == -1 ("no scroll information") half of set_cursor's contract only
-- holds when CursorMoved fires inside the API call, which depends on the
-- embed (with ui_attach it does; headless it does not) - it is not testable
-- here. What the echo always carries is the new position:
vim.cmd('doautocmd CursorMoved')
state = t.report(h, 'vsneo_state')
t.expect(state ~= nil, 'no vsneo_state echo after set_cursor')
t.eq(state[3], 49, 'set_cursor echo should carry the new line, 0-based')
t.eq(state[4], 4, 'set_cursor echo should carry the new column')

print('state_push_tests: ALL OK')
