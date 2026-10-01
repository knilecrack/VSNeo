-- ar / ir text objects and ]r [r ]R [R motions over the regions Visual
-- Studio mirrors in as folds (folds_set). Linewise; a count on ar picks an
-- outer region; ir drops the header and closing line.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/regions.lua' })

local lines = {}
for i = 1, 50 do lines[i] = 'line ' .. i end
vim.api.nvim_buf_set_lines(0, 0, -1, false, lines)

-- A class 10-40 holding two methods, 12-20 and 25-35; a #region 44-48.
vsneo.folds_set('C:/test/regions.lua', { 10, 40, false, 12, 20, false, 25, 35, false, 44, 48, false })

local function feed(keys)
  -- 'x' without 'n': the mappings under test must apply.
  vim.api.nvim_feedkeys(vim.api.nvim_replace_termcodes(keys, true, false, true), 'x', false)
end

local function visual_rows()
  local s = vim.fn.line('v')
  local e = vim.fn.line('.')
  if s > e then s, e = e, s end
  return s, e, vim.api.nvim_get_mode().mode
end

-- ar: the innermost region around the cursor, linewise.
vim.api.nvim_win_set_cursor(0, { 15, 0 })
feed('var')
local s, e, mode = visual_rows()
t.eq(mode, 'V', 'ar selects linewise')
t.eq(s, 12, 'ar: the inner method starts at 12')
t.eq(e, 20, 'ar: and ends at 20')
feed('<Esc>')

-- 2ar: the next region out.
vim.api.nvim_win_set_cursor(0, { 15, 0 })
feed('v2ar')
s, e = visual_rows()
t.eq(s, 10, '2ar: the class starts at 10')
t.eq(e, 40, '2ar: and ends at 40')
feed('<Esc>')

-- ir: without the header and the closing line.
vim.api.nvim_win_set_cursor(0, { 30, 0 })
feed('vir')
s, e = visual_rows()
t.eq(s, 26, 'ir: first line inside the method')
t.eq(e, 34, 'ir: last line inside the method')
feed('<Esc>')

-- Under an operator: dar deletes the whole region's lines.
vim.api.nvim_win_set_cursor(0, { 46, 0 })
feed('dar')
t.eq(vim.api.nvim_buf_line_count(0), 45, 'dar deleted the 5-line #region')
t.eq(vim.api.nvim_buf_get_lines(0, 43, 44, false)[1], 'line 49', 'the line after the region moved up')

-- Restore (set_lines and the operator share one undo block here, so u is
-- not the way back).
local function restore()
  vim.api.nvim_buf_set_lines(0, 0, -1, false, lines)
  -- Replacing every line drops nvim's manual folds, and an identical
  -- folds_set is a no-op by design: clear first so the regions come back.
  vsneo.folds_set('C:/test/regions.lua', {})
  vsneo.folds_set('C:/test/regions.lua', { 10, 40, false, 12, 20, false, 25, 35, false, 44, 48, false })
end
restore()
t.eq(vim.api.nvim_buf_line_count(0), 50, 'restored')

-- Outside every region: nothing happens.
vim.api.nvim_win_set_cursor(0, { 3, 0 })
feed('var')
t.eq(vim.api.nvim_get_mode().mode, 'v', 'ar outside a region leaves the selection alone')
feed('<Esc>')

-- Motions.
vim.api.nvim_win_set_cursor(0, { 1, 0 })
feed(']r')
t.eq(vim.api.nvim_win_get_cursor(0)[1], 10, ']r: the first region start')
feed(']r')
t.eq(vim.api.nvim_win_get_cursor(0)[1], 12, ']r: the next start, nested or not')
feed('2]r')
t.eq(vim.api.nvim_win_get_cursor(0)[1], 44, '2]r: counts')
feed('[r')
t.eq(vim.api.nvim_win_get_cursor(0)[1], 25, '[r: the previous start')
feed(']R')
t.eq(vim.api.nvim_win_get_cursor(0)[1], 35, ']R: the next region end')
feed('[R')
t.eq(vim.api.nvim_win_get_cursor(0)[1], 20, '[R: the previous region end')
feed("''")
t.eq(vim.api.nvim_win_get_cursor(0)[1], 35, "'' returns: the motion set a jumplist entry")

-- An operator over a motion is linewise.
vim.api.nvim_win_set_cursor(0, { 42, 0 })
feed('d]r')
t.eq(vim.api.nvim_buf_line_count(0), 47, 'd]r deleted lines 42-44 as whole lines')
restore()

print('region_textobject_tests: ALL OK')
