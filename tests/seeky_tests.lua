-- vsneo.seeky(mode, query): asks the extension to show the embedded Seeky
-- picker by sending the vsneo_seeky notification (mode, query-or-'').
-- :Seeky [mode] [query] is the command form over the same call.

local t = dofile('tests/helper.lua')
local handle = t.setup({ name = 'C:/test/seeky.lua' })

t.eq(vsneo.seeky('files'), true, 'files returns true')
t.eq(vsneo.seeky('grep', 'foo bar|baz'), true, 'grep with query returns true')
t.eq(vsneo.seeky('symbols'), true, 'symbols returns true')

local all = t.reports(handle, 'vsneo_seeky')
t.eq(#all, 3, 'one notification per call')
t.eq(all[1][2], 'files', 'mode is the first argument')
t.eq(all[1][3], '', 'missing query sends the empty string')
t.eq(all[2][2], 'grep', 'grep mode')
t.eq(all[2][3], 'foo bar|baz', 'query is verbatim, pipes included')
t.eq(all[3][2], 'symbols', 'symbols mode')
t.eq(all[3][3], '', 'missing query sends the empty string')

-- The pick lands far away: the call records the jump, so '' comes back.
vim.api.nvim_win_set_cursor(0, { 40, 0 })
vsneo.seeky('files')
vim.api.nvim_win_set_cursor(0, { 70, 0 })
vim.cmd("normal! ''")
t.eq(vim.api.nvim_win_get_cursor(0)[1], 40, "'' returns to where the picker was opened")

-- :Seeky with no argument opens files; the rest of the line is the query.
local function last()
  local r = t.report(handle, 'vsneo_seeky')
  return r[2], r[3]
end

vim.cmd('Seeky')
local mode, query = last()
t.eq(mode, 'files', ':Seeky alone opens files')
t.eq(query, '', ':Seeky alone has no query')

vim.cmd('Seeky grep  foo  bar')
mode, query = last()
t.eq(mode, 'grep', ':Seeky grep')
t.eq(query, 'foo  bar', 'query is the rest of the line, inner spacing kept')

vim.cmd('Seeky outline')
mode = last()
t.eq(mode, 'outline', 'outline is passed through (the extension maps it to the page mode)')

-- From visual mode with no query, the selection's first line is the query.
vim.api.nvim_buf_set_lines(0, 0, 2, false, { '  needle in hay', 'second line' })
vim.api.nvim_win_set_cursor(0, { 1, 2 })
vim.cmd('normal! vee\27')
vim.cmd("'<,'>Seeky grep")
mode, query = last()
t.eq(mode, 'grep', 'range form keeps the mode')
t.eq(query, 'needle in', 'range form greps the selection')

vim.cmd('normal! Vj\27')
vim.cmd("'<,'>Seeky grep")
_, query = last()
t.eq(query, 'needle in hay', 'a multi-line selection takes its first line, trimmed')

vim.cmd("'<,'>Seeky grep explicit")
_, query = last()
t.eq(query, 'explicit', 'an explicit query wins over the range')

vim.cmd('Seeky lines')
mode = last()
t.eq(mode, 'lines', 'lines (current file) is passed through')

vim.cmd('Seeky resume')
mode, query = last()
t.eq(mode, 'resume', 'resume is passed through')
t.eq(query, '', 'resume carries no query')

-- Completion offers modes for the first argument only.
local modes = vim.fn.getcompletion('Seeky o', 'cmdline')
t.eq(#modes, 1, 'one mode starts with o')
t.eq(modes[1], 'outline', 'outline completes')
t.eq(#vim.fn.getcompletion('Seeky grep fo', 'cmdline'), 0, 'no completion inside the query')
local r = vim.fn.getcompletion('Seeky r', 'cmdline')
t.eq(#r, 1, 'one mode starts with r')
t.eq(r[1], 'resume', 'resume completes')

print('seeky_tests: ALL OK')
