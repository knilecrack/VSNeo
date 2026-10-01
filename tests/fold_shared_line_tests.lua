-- Sibling regions that share a line. Visual Studio's regions start and end
-- mid-line: an 'if { ... }' ends on the line where '} else {' starts the
-- next region. nvim's folds are whole lines, so the earlier region's fold
-- stops one line short; otherwise the two tangled, collapsing the else
-- also closed the if, and j/k could not get past (seen live: VS collapsed
-- 73-78, nvim reported 68-73 and 73-78 closed).

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/shared.lua' })

local lines = {}
for i = 1, 90 do lines[i] = 'line ' .. i end
vim.api.nvim_buf_set_lines(0, 0, -1, false, lines)
local path = 'C:/test/shared.lua'

local function feed(k) vim.api.nvim_feedkeys(vim.api.nvim_replace_termcodes(k, true, false, true), 'x', false) end

-- method 60-85 > if 68-73, else 73-78
vsneo.folds_set(path, { 60, 85, false, 68, 73, false, 73, 78, false })
t.expect(vim.fn.foldlevel(73) == 2, 'line 73 belongs to the else fold only (level 2, not 3)')

-- Collapse the else, the way a mouse click sends it.
t.clear(h)
vsneo.fold_closed(path, 73, 78)
t.eq(vim.fn.foldclosed(73), 73, 'the else is closed')
t.eq(vim.fn.foldclosed(68), -1, 'the if stays open')
t.eq(vim.fn.foldclosed(60), -1, 'the method stays open')
vim.api.nvim_win_set_cursor(0, { 72, 0 })
vim.api.nvim_win_set_cursor(0, { 71, 0 })
local r = t.report(h, 'vsneo_folds_changed')
t.expect(r == nil, 'nothing reported back: ' .. vim.inspect(r))

vim.api.nvim_win_set_cursor(0, { 72, 0 })
feed('j')
t.eq(vim.fn.line('.'), 73, 'j lands on the closed else')
feed('j')
t.eq(vim.fn.line('.'), 79, 'j again passes it')
feed('k')
t.eq(vim.fn.line('.'), 73, 'k comes back onto it')

-- Collapse the if as well: the else stays its own fold.
vsneo.fold_closed(path, 68, 73)
t.eq(vim.fn.foldclosed(68), 68, 'the if is closed')
t.eq(vim.fn.foldclosed(73), 73, 'the else is still its own closed fold')
vsneo.fold_opened(path, 73)
t.eq(vim.fn.foldclosed(73), -1, 'opening the else')
t.eq(vim.fn.foldclosed(68), 68, 'leaves the if closed')

-- An identical full push is still a no-op (the fitting is deterministic).
vsneo.fold_opened(path, 68)
vsneo.folds_set(path, { 60, 85, false, 68, 73, false, 73, 78, false })
vim.cmd('73foldclose')
vsneo.folds_set(path, { 60, 85, false, 68, 73, false, 73, 78, false })
t.eq(vim.fn.foldclosed(73), 73, 'an identical push does not rebuild over a user close')

print('fold_shared_line_tests: ALL OK')
