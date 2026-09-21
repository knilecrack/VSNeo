-- Multi-edit: vsneo.multi_edit() arms the matches of @/ as extmarks; the
-- next insert-change (cgn/ciw + VS-typed text + Esc) is replayed at every
-- other stored match when it is captured. Same <F14> harness trick as
-- dot_repeat_tests: API text insertion stands in for typing in VS.

local t = dofile('tests/helper.lua')
local h = t.setup({ lines = { 'placeholder' } })

local function tc(s) return vim.api.nvim_replace_termcodes(s, true, false, true) end

local typed = nil
local jump_far = false  -- simulate a caret push/click racing the capture
vim.keymap.set('i', '<F14>', function()
  local cur = vim.api.nvim_win_get_cursor(0)
  local parts = vim.split(typed, '\n', { plain = true })
  vim.api.nvim_buf_set_text(0, cur[1] - 1, cur[2], cur[1] - 1, cur[2], parts)
  if jump_far then
    vim.api.nvim_win_set_cursor(0, { vim.api.nvim_buf_line_count(0), 0 })
    return ''
  end
  local row = cur[1] + #parts - 1
  local col = (#parts == 1) and cur[2] + #parts[#parts] or #parts[#parts]
  vim.api.nvim_win_set_cursor(0, { row, col })
  return ''
end)

local function change(keys, text)
  typed = text
  vim.fn.feedkeys(tc(keys .. '<F14>'), 'x')
  vim.wait(50)  -- capture schedule and the armed replay both run here
end

local function scene(new_lines)
  vim.api.nvim_buf_set_lines(0, 0, -1, false, new_lines)
  vim.api.nvim_win_set_cursor(0, { 1, 0 })
  vim.wait(20)
end
local function all() return table.concat(vim.api.nvim_buf_get_lines(0, 0, -1, false), '|') end

-- the headline case: arm, change the first match, Esc replays the rest
scene({ 'alpha target omega', 'beta  target gamma', 'delta target sigma' })
vim.cmd('silent /target')
vim.api.nvim_win_set_cursor(0, { 1, 0 })
vsneo.multi_edit()
change('cgn', 'HIT')
t.eq(all(), 'alpha HIT omega|beta  HIT gamma|delta HIT sigma',
  'one cgn replays to every match')

-- a replacement that itself matches the pattern must terminate
scene({ 'target a', 'target b' })
vim.cmd('silent /target')
vim.api.nvim_win_set_cursor(0, { 1, 0 })
vsneo.multi_edit()
change('cgn', 'targeted')
t.eq(all(), 'targeted a|targeted b', 'self-matching replacement cannot loop')

-- non-gn change keys replay at the stored positions too
scene({ 'foo = 1', 'x = foo', 'y = foo + foo' })
vim.cmd('silent /foo')
vim.api.nvim_win_set_cursor(0, { 1, 0 })
vsneo.multi_edit()
change('ciw', 'bar')
t.eq(all(), 'bar = 1|x = bar|y = bar + bar', 'ciw replays at every stored match')

-- one-shot: the arming is consumed, a later change does not replay
scene({ 'foo = 1', 'x = foo', 'zap here' })
vim.cmd('silent /foo')
vim.api.nvim_win_set_cursor(0, { 1, 0 })
vsneo.multi_edit()
change('ciw', 'bar')
t.eq(all(), 'bar = 1|x = bar|zap here', 'armed replay happened once')
vim.api.nvim_win_set_cursor(0, { 3, 0 })
change('ciw', 'ZAP')
t.eq(all(), 'bar = 1|x = bar|ZAP here', 'no replay without a fresh arming')

-- a caret that jumps during insert (mouse click, racing caret push) makes
-- the captured "typed text" a whole buffer span: reject, never replay
jump_far = true
scene({ 'foo 1', 'foo 2', 'foo 3', 'foo 4', 'foo 5', 'foo 6', 'foo 7', 'foo 8' })
vim.cmd('silent /foo')
vim.api.nvim_win_set_cursor(0, { 1, 0 })
vsneo.multi_edit()
change('ciw', 'bar')
t.eq(all(), 'bar 1|foo 2|foo 3|foo 4|foo 5|foo 6|foo 7|foo 8',
  'oversized capture rejected: only the manual change stands')
jump_far = false

-- a match at buffer position (1,0) is armed too (the arming search must
-- accept the match under the cursor on its first pass)
scene({ 'foo a', 'foo b' })
vim.cmd('silent /foo')
vim.api.nvim_win_set_cursor(0, { 2, 0 })
vsneo.multi_edit()
change('ciw', 'bar')
t.eq(all(), 'bar a|bar b', 'match at (1,0) replays when the manual change is elsewhere')

-- a match at buffer position (1,0) is armed too (the arming search must
-- accept the match under the cursor on its first pass)
scene({ 'foo a', 'foo b' })
vim.cmd('silent /foo')
vim.api.nvim_win_set_cursor(0, { 2, 0 })
vsneo.multi_edit()
change('ciw', 'bar')
t.eq(all(), 'bar a|bar b', 'match at (1,0) replays when the manual change is elsewhere')

-- matches spanning lines are refused at arm time: their replay deletes
-- across the boundary and the fan-out eats the file line by line
scene({ 'x o', 'b y', 'foo one', 'foo two' })
vim.fn.setreg('/', '\\v(o\\nb|foo)')
vim.api.nvim_win_set_cursor(0, { 3, 0 })
vsneo.multi_edit()
change('ciw', 'bar')
t.eq(all(), 'x o|b y|bar one|bar two', 'multi-line match skipped, single-line matches replayed')

print('multi_edit_tests: OK')
