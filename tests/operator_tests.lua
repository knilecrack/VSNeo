-- vsneo.operator: a Visual Studio command as a Vim operator. The mapping
-- arms operatorfunc and returns g@, nvim reads the motion, and the range
-- goes to the extension as vsneo_range_action
-- [command, kind, startRow, startByte, endRow, endByte] (rows 1-based,
-- byte columns 0-based, end inclusive). gc and = are the defaults.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/operator.lua' })

vim.api.nvim_buf_set_lines(0, 0, -1, false, { 'one two', 'three', 'four', 'five' })

local function run(keys)
  t.clear(h)
  vim.api.nvim_feedkeys(vim.api.nvim_replace_termcodes(keys, true, false, true), 'x', false)
  return t.report(h, 'vsneo_range_action')
end

vim.api.nvim_win_set_cursor(0, { 2, 0 })
local r = run('gcj')
t.expect(r ~= nil, 'gcj sends a range action')
t.eq(r[2], 'Edit.CommentSelection', 'gc runs the comment command')
t.eq(r[3], 'line', 'j is a linewise motion')
t.eq(r[4], 2, 'from the cursor line')
t.eq(r[6], 3, 'to the line below')

r = run('gcc')
t.eq(r[3], 'line', 'gcc is linewise')
t.eq(r[4], 2, 'gcc: the current line')
t.eq(r[6], 2, 'gcc: only that line')

r = run('3gcc')
t.eq(r[6], 4, 'a count extends gcc')

vim.api.nvim_win_set_cursor(0, { 1, 0 })
r = run('=e')
t.eq(r[2], 'Edit.FormatSelection', '= runs the format command')
t.eq(r[3], 'char', 'e is a charwise motion')
t.eq(r[4], 1, 'charwise: start row')
t.eq(r[5], 0, 'charwise: start byte')
t.eq(r[6], 1, 'charwise: end row')
t.eq(r[7], 2, 'charwise: end byte, inclusive (the e of one)')

r = run('Vj=')
t.eq(r[3], 'line', 'visual line mode gives a linewise range')
t.eq(r[4], 1, 'visual: from the first selected line')
t.eq(r[6], 2, 'visual: to the last')

-- Dot repeat: g@ with an operatorfunc is natively repeatable.
vim.api.nvim_win_set_cursor(0, { 3, 0 })
run('gcj')
vim.api.nvim_win_set_cursor(0, { 1, 0 })
r = run('.')
t.expect(r ~= nil, '. repeats the operator')
t.eq(r[2], 'Edit.CommentSelection', '. repeats the same command')
t.eq(r[4], 1, '. applies at the new position')
t.eq(r[6], 2, '. keeps the motion')

-- A user-defined one.
vim.keymap.set({ 'n', 'x' }, 'gs', vsneo.operator('Edit.SortLines'), { expr = true })
vim.api.nvim_win_set_cursor(0, { 1, 0 })
r = run('gsG')
t.eq(r[2], 'Edit.SortLines', 'vsneo.operator binds any command')
t.eq(r[4], 1, 'gsG: from the first line')
t.eq(r[6], 4, 'gsG: to the last')

print('operator_tests: ALL OK')
