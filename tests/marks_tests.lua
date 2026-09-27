-- vsneo_marks: the marks Visual Studio draws on the scrollbar, as [path, list]
-- with one [0-based line, name] per mark (a-z of the current buffer, and A-Z
-- that point into it). Sent a moment after a key, only when the list changed.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/marks.lua' })

local function wait_for_marks()
  local found
  vim.wait(500, function()
    found = t.report(h, 'vsneo_marks')
    return found ~= nil
  end, 10)
  return found
end

-- The empty list is only sent if it differs from nothing sent yet: set the
-- first mark and look.
t.clear(h)
vim.api.nvim_win_set_cursor(0, { 5, 0 })
vim.cmd('normal! ma')
local r = wait_for_marks()
t.expect(r ~= nil, 'setting a mark is reported')
t.eq(r[2], vim.api.nvim_buf_get_name(0), 'the path of the buffer, as nvim names it')
t.eq(#r[3], 1, 'one mark')
t.eq(r[3][1][1], 4, '0-based line')
t.eq(r[3][1][2], 'a', 'its name')

-- A second mark and a file mark, sorted by line.
t.clear(h)
vim.api.nvim_win_set_cursor(0, { 2, 0 })
vim.cmd('normal! mB')
r = wait_for_marks()
t.expect(r ~= nil, 'a file mark into this buffer is reported')
t.eq(#r[3], 2, 'both marks')
t.eq(r[3][1][2], 'B', 'sorted by line: B on line 2 first')
t.eq(r[3][2][2], 'a', 'then a on line 5')

-- A key that changes no mark sends nothing.
t.clear(h)
vim.cmd('normal! j')
vim.wait(150)
t.eq(t.report(h, 'vsneo_marks'), nil, 'an unchanged list is not re-sent')

-- Edits move marks; the new line is reported.
t.clear(h)
vim.api.nvim_buf_set_lines(0, 0, 0, false, { 'inserted' })
vim.api.nvim_exec_autocmds('TextChanged', {})
r = wait_for_marks()
t.expect(r ~= nil, 'an edit that shifts marks is reported')
t.eq(r[3][2][1], 5, 'a moved down a line')

-- Deleting marks clears them.
t.clear(h)
vim.cmd('delmarks a B')
vim.api.nvim_exec_autocmds('TextChanged', {})
r = wait_for_marks()
t.expect(r ~= nil, 'deleting marks is reported')
t.eq(#r[3], 0, 'no marks left')

print('marks_tests: ok')
