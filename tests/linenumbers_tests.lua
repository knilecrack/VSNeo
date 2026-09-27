-- vsneo_linenumbers: [number, relativenumber] for RelativeLineNumberMargin.
-- The margin draws the cursor line's absolute number only when 'number' is
-- on (Vim's rule; relativenumber alone shows 0), so a stale 'number' shows
-- as a 0 on the cursor line.

local t = dofile('tests/helper.lua')

-- A buffer displayed before the rc runs: nvim remembers its window options
-- and restores them when it comes back, without OptionSet.
local early = vim.api.nvim_create_buf(true, false)
local startup = vim.api.nvim_get_current_buf()
vim.api.nvim_win_set_buf(0, early)
vim.api.nvim_win_set_buf(0, startup)

local h = t.setup({ name = 'C:/test/linenumbers.lua' })
local r = t.report(h, 'vsneo_linenumbers')
t.expect(r ~= nil, 'pushed at load')
t.eq(r[2], 0, 'number off by default')
t.eq(r[3], 0, 'relativenumber off by default')

-- The rc, via :source (OptionSet does not fire during startup).
local path = vim.fn.tempname() .. '.vim'
vim.fn.writefile({ 'set number relativenumber' }, path)
t.clear(h)
vim.cmd('source ' .. vim.fn.fnameescape(path))
r = t.report(h, 'vsneo_linenumbers')
t.expect(r ~= nil, 're-sent after :source')
t.eq(r[2], 1, 'number on after the rc')
t.eq(r[3], 1, 'relativenumber on after the rc')

-- Switching to the early buffer must not bring back its pre-rc options.
t.clear(h)
vim.api.nvim_win_set_buf(0, early)
t.eq(vim.wo.number, true, "the early buffer takes the ':set' number")
t.eq(vim.wo.relativenumber, true, "the early buffer takes the ':set' relativenumber")
r = t.report(h, 'vsneo_linenumbers')
t.expect(r == nil or (r[2] == 1 and r[3] == 1), 'no stale push on the switch')

-- Live toggles still arrive, and are not repeated when nothing changed.
t.clear(h)
vim.cmd('set nonumber')
r = t.report(h, 'vsneo_linenumbers')
t.expect(r ~= nil and r[2] == 0 and r[3] == 1, ':set nonumber is pushed')
t.clear(h)
vim.api.nvim_win_set_buf(0, startup)
t.eq(#t.reports(h, 'vsneo_linenumbers'), 0, 'an unchanged switch sends nothing')
t.eq(vim.wo.number, false, "the switch follows ':set nonumber' too")

print('linenumbers_tests: ok')
