-- Escape always ends with a state push, even when it changes nothing.
-- The extension's mode cache moves only on a push; if it ever read Insert
-- while nvim sat in normal, an Escape that fired no ModeChanged left it
-- stuck there and every key after it passed through as typed text.

local t = dofile('tests/helper.lua')
local h = t.setup({})

local function tc(s) return vim.api.nvim_replace_termcodes(s, true, false, true) end

-- Escape in normal mode: no mode change, no cursor move, still a push.
vim.api.nvim_win_set_cursor(0, { 5, 2 })
vim.wait(20)
t.clear(h)
vim.fn.feedkeys(tc('<Esc>'), 'x')
vim.wait(50)
local state = t.report(h, 'vsneo_state')
t.expect(state ~= nil, 'no vsneo_state after <Esc> in normal mode')
t.eq(state[2], 'n', 'pushed mode after <Esc> in normal')
t.eq(state[3], 4, 'pushed line is the cursor line (0-based)')

-- Escape leaving insert: the last push says normal.
t.clear(h)
vim.fn.feedkeys(tc('i<Esc>'), 'x')
vim.wait(50)
state = t.report(h, 'vsneo_state')
t.expect(state ~= nil, 'no vsneo_state after i<Esc>')
t.eq(state[2], 'n', 'last pushed mode after leaving insert')

-- Other keys do not gain an extra push: a motion that cannot move pushes nothing.
vim.api.nvim_win_set_cursor(0, { 1, 0 })
vim.wait(20)
t.clear(h)
vim.fn.feedkeys('k', 'x')
vim.wait(50)
t.eq(#t.reports(h, 'vsneo_state'), 0, 'no push for a key that changed nothing')

print('esc_push_tests: OK')
