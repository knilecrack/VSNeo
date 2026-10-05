-- Folds after a whole-buffer replacement. A re-prime (set_all_lines) drops
-- nvim's manual folds with the lines; the companion must forget its agreed
-- regions so the extension's full push afterwards rebuilds them, and an
-- "identical" push must rebuild anyway when the folds are gone.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/reprime.lua' })

local lines = {}
for i = 1, 30 do lines[i] = 'line ' .. i end
vim.api.nvim_buf_set_lines(0, 0, -1, false, lines)

local list = { 5, 10, true, 15, 25, false }
vsneo.folds_set('C:/test/reprime.lua', list)
t.eq(vim.fn.foldclosed(5), 5, 'fold 5-10 closed')
t.expect(vim.fn.foldlevel(15) > 0, 'fold 15-25 exists')

-- A re-prime: every line replaced. Which manual folds survive that depends
-- on the replacement (nvim adjusts some, drops others); the contract is
-- only that the full push after the prime leaves every region in place.
local longer = vim.deepcopy(lines)
for i = 31, 40 do longer[i] = 'line ' .. i end
vsneo.set_all_lines(vim.api.nvim_get_current_buf(), longer)
vsneo.folds_set('C:/test/reprime.lua', list)
t.eq(vim.fn.foldclosed(5), 5, 'after the prime and its push: the closed fold is there')
t.expect(vim.fn.foldlevel(15) > 0, 'and the open one')

-- Folds lost some other way (zE) with the agreed list intact: an identical
-- push still rebuilds, because the folds do not exist.
vim.cmd('normal! zE')
t.eq(vim.fn.foldlevel(5), 0, 'zE removed the folds')
vsneo.folds_set('C:/test/reprime.lua', list)
t.expect(vim.fn.foldlevel(5) > 0 and vim.fn.foldlevel(15) > 0, 'an identical push rebuilds missing folds')

-- A surviving parent fold does not prove that a nested region still exists.
local nested = { 5, 25, true, 10, 15, true }
vsneo.folds_set('C:/test/reprime.lua', nested)
vim.cmd('5foldopen')
-- No Ex command deletes a fold; zd deletes the innermost one at the cursor.
vim.api.nvim_win_set_cursor(0, { 10, 0 })
vim.cmd('normal! zd')
t.eq(vim.fn.foldlevel(10), 1, 'scene setup: the parent fold survives')
vsneo.folds_set('C:/test/reprime.lua', nested)
t.eq(vim.fn.foldlevel(10), 2, 'an identical push rebuilds a missing nested fold')

-- And when nothing is missing, the identical push is still a no-op: the
-- closed state the user changed stays.
vim.cmd('5foldopen')
vsneo.folds_set('C:/test/reprime.lua', nested)
t.eq(vim.fn.foldclosed(5), -1, 'an identical push over intact folds changes nothing')

-- Visual Studio has no regions left after a drift repair: the empty push
-- equals the reset agreed list, but a same-length replacement kept nvim's
-- manual folds, so it must still clear them.
vsneo.folds_set('C:/test/reprime.lua', list)
t.expect(vim.fn.foldlevel(5) > 0, 'scene setup: folds exist')
vsneo.set_all_lines(vim.api.nvim_get_current_buf(), vim.deepcopy(lines))
vsneo.folds_set('C:/test/reprime.lua', {})
t.eq(vim.fn.foldlevel(5), 0, 'an empty push after a repair clears surviving folds')

-- set_all_lines on another buffer leaves this window's agreed list alone.
local other = vim.api.nvim_create_buf(true, false)
vsneo.set_all_lines(other, { 'x' })
vim.cmd('normal! zE')
vsneo.folds_set('C:/test/reprime.lua', list)
t.expect(vim.fn.foldlevel(5) > 0, 'still rebuilt from the existence check')

print('fold_reprime_tests: ALL OK')
