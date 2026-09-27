-- Search highlight scan: only the lines around nvim's window are scanned and
-- sent (vsneo_search_matches), and moving the window past the scanned range
-- rescans.

local lines = {}
for i = 1, 5000 do lines[i] = 'x foo ' .. i end

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/search.lua', lines = lines })

vim.o.hlsearch = true
vim.fn.setreg('/', 'foo')
vim.v.hlsearch = 1

local function last_matches()
  local r = t.report(h, 'vsneo_search_matches')
  return r and r[2] or nil
end

local function trigger(event)
  t.clear(h)
  vim.api.nvim_exec_autocmds(event, {})
  vim.wait(300, function() return last_matches() ~= nil end)
end

-- At the top of the file: the window plus the margin, not all 5000 lines.
vim.api.nvim_win_set_cursor(0, { 1, 0 })
trigger('TextChanged')
local m = last_matches()
t.expect(m ~= nil, 'an edit should send matches')
local w1 = vim.fn.line('w$')
t.expect(#m < 5000, 'scan should not cover the whole buffer (got ' .. #m .. ')')
t.expect(#m >= w1, 'every visible line should be covered')
t.eq(m[1][1], 0, 'first match on line 0')
t.eq(m[1][2], 2, 'match start byte')
t.eq(m[1][3], 5, 'match end byte')

-- Far down the file: the visible lines there are covered, with the right
-- 0-based line numbers.
vim.api.nvim_win_set_cursor(0, { 4000, 0 })
vim.cmd('normal! zt')
trigger('CursorMoved')
m = last_matches()
t.expect(m ~= nil, 'moving out of the scanned range should rescan')
local w0 = vim.fn.line('w0')
local seen = {}
for _, x in ipairs(m) do seen[x[1]] = true end
for l = w0, vim.fn.line('w$') do
  t.expect(seen[l - 1], 'visible line ' .. l .. ' should have its match')
end
t.expect(not seen[0], 'lines far above the window should not be scanned')

-- Small move inside the scanned range, no edit: nothing is resent.
vim.api.nvim_win_set_cursor(0, { 4001, 0 })
t.clear(h)
vim.api.nvim_exec_autocmds('CursorMoved', {})
vim.wait(150)
t.eq(last_matches(), nil, 'a move within the scanned range should not rescan')

-- :nohlsearch still clears.
vim.v.hlsearch = 0
trigger('TextChanged')
t.eq(#last_matches(), 0, 'hlsearch off should send an empty list')

print('search_scan_tests: ALL OK')
