-- fold_closed on a nested region closes that region only. It used to run
-- 'foldclose!', which closes every fold containing the line - the class's
-- header line is inside the namespace too, so collapsing the class in
-- Visual Studio closed the namespace, and the state push then reported
-- every region inside it closed.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/nested.lua' })

local lines = {}
for i = 1, 40 do lines[i] = 'line ' .. i end
vim.api.nvim_buf_set_lines(0, 0, -1, false, lines)

-- namespace 1-40 > class 5-30 > ctor 8-15 > if 10-12 ; func 32-36
local path = 'C:/test/nested.lua'
vsneo.folds_set(path, { 1, 40, false, 5, 30, false, 8, 15, false, 10, 12, false, 32, 36, false })

-- Collapse the class in Visual Studio.
t.clear(h)
vsneo.fold_closed(path, 5, 30)
t.eq(vim.fn.foldclosed(5), 5, 'the class is closed')
t.eq(vim.fn.foldclosed(1), -1, 'the namespace stays open')
t.eq(vim.fn.foldclosed(32), -1, 'the sibling function stays open')
t.expect(t.report(h, 'vsneo_folds_changed') == nil, 'nothing to report back: the push was Visual Studio\'s own')

-- Open it again, collapse the innermost if.
vsneo.fold_opened(path, 5)
t.eq(vim.fn.foldclosed(5), -1, 'the class is open again')
vsneo.fold_closed(path, 10, 12)
t.eq(vim.fn.foldclosed(10), 10, 'the if is closed')
t.eq(vim.fn.foldclosed(8), -1, 'the ctor stays open')
t.eq(vim.fn.foldclosed(5), -1, 'the class stays open')
t.eq(vim.fn.foldclosed(1), -1, 'the namespace stays open')

-- Closing an already closed region is a no-op, not a step outward.
vsneo.fold_closed(path, 10, 12)
t.eq(vim.fn.foldclosed(8), -1, 'a repeat close does not reach the ctor')
vsneo.fold_opened(path, 10)

-- Collapse the namespace with everything inside it open. The regions inside
-- are hidden, not closed: the state push must not report them closed (that
-- is what collapsed every block in a file a second after a mouse click),
-- and expanding the namespace again shows them open.
t.clear(h)
vsneo.fold_closed(path, 1, 40)
t.eq(vim.fn.foldclosed(1), 1, 'the namespace is closed')
t.eq(vim.fn.foldclosed(5), 1, 'the class is hidden inside it (nvim reports the outer fold)')
vim.api.nvim_win_set_cursor(0, { 2, 0 })   -- a cursor move: the push with fold detection
vim.api.nvim_win_set_cursor(0, { 1, 0 })
local report = t.report(h, 'vsneo_folds_changed')
t.expect(report == nil, 'nothing inside the namespace was reported as closed: ' .. vim.inspect(report))

vsneo.fold_opened(path, 1)
t.eq(vim.fn.foldclosed(1), -1, 'the namespace is open again')
t.eq(vim.fn.foldclosed(5), -1, 'the class is still open')
t.eq(vim.fn.foldclosed(8), -1, 'the ctor is still open')
t.eq(vim.fn.foldclosed(10), -1, 'the if is still open')
t.eq(vim.fn.foldclosed(32), -1, 'the function is still open')

-- A full push with a closed namespace and an open class inside: the class
-- keeps the requested state while hidden, and is open once the namespace
-- opens.
vsneo.folds_set(path, { 1, 40, true, 5, 30, false, 8, 15, true, 10, 12, false, 32, 36, false })
t.eq(vim.fn.foldclosed(1), 1, 'full push: namespace closed')
vsneo.fold_opened(path, 1)
t.eq(vim.fn.foldclosed(5), -1, 'full push: the class inside was open')
t.eq(vim.fn.foldclosed(8), 8, 'full push: the ctor inside was closed')
t.eq(vim.fn.foldclosed(32), -1, 'full push: the function was open')

print('fold_nested_close_tests: ALL OK')
