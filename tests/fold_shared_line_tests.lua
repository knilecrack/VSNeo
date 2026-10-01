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

local function feed(k)
  vim.api.nvim_feedkeys(vim.api.nvim_replace_termcodes(k, true, false, true), 'x', false)
  -- A headless -l script runs keys without the main loop that fires
  -- CursorMoved; fire it as the editor would, so the state push runs.
  vim.api.nvim_exec_autocmds('CursorMoved', {})
end

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

-- The live failure: a collapsed region whose last line it shares with the
-- next region (a parameter list ending ') {' or ')' before a body). In
-- Visual Studio that line is part of the collapsed line, so the cursor
-- must never rest on it: j from the header goes past it, k from below
-- comes back to the header. Before, j landed on it, Visual Studio snapped
-- the caret back to the header, and j could never get past.
vsneo.folds_set(path, {})
vsneo.folds_set(path, { 60, 85, false, 68, 73, false, 73, 78, false })
vsneo.fold_closed(path, 68, 73)
t.eq(vim.fn.foldclosed(68), 68, 'the parameter list (68-73, fitted to 68-72) is closed')
vim.api.nvim_win_set_cursor(0, { 67, 0 })
feed('j')
t.eq(vim.fn.line('.'), 68, 'j from above lands on the header')
feed('j')
t.eq(vim.fn.line('.'), 74, 'j from the header passes the shared line 73')
feed('k')
t.eq(vim.fn.line('.'), 68, 'k from below the shared line comes back to the header')
feed('k')
t.eq(vim.fn.line('.'), 67, 'k from the header goes above')
-- Open again: the shared line is an ordinary line once more.
vsneo.fold_opened(path, 68)
vim.api.nvim_win_set_cursor(0, { 72, 0 })
feed('j')
t.eq(vim.fn.line('.'), 73, 'with the region open, j stops on line 73 as usual')

print('fold_shared_line_tests: ALL OK')
