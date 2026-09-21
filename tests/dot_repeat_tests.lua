-- Dot-repeat for changes whose text never reached nvim (the passthrough
-- problem): cgn/cw/o + text typed in Visual Studio, then '.' must repeat
-- the whole change, not just the deletion.
--
-- Harness trick: insert mode cannot be held open once a feedkeys stream
-- runs out, and API calls mid-stream need a non-expr mapping. <F14> in
-- insert mode stands in for text typed in Visual Studio: the mirror
-- applies it (nvim_buf_set_text) and the caret push follows
-- (CursorSynchronizer), so the cursor lands at the insertion end - exactly
-- what production looks like from nvim's side.

local t = dofile('tests/helper.lua')
local h = t.setup({ lines = { 'alpha target omega', 'beta  target gamma',
                              'delta target sigma' } })

local function tc(s) return vim.api.nvim_replace_termcodes(s, true, false, true) end

local typed = nil
vim.keymap.set('i', '<F14>', function()
  local cur = vim.api.nvim_win_get_cursor(0)
  local parts = vim.split(typed, '\n', { plain = true })
  vim.api.nvim_buf_set_text(0, cur[1] - 1, cur[2], cur[1] - 1, cur[2], parts)
  local row = cur[1] + #parts - 1
  local col = (#parts == 1) and cur[2] + #parts[#parts] or #parts[#parts]
  vim.api.nvim_win_set_cursor(0, { row, col })
  return ''
end)

-- One full change session: change keys, then the "typed" text, then the
-- feed ends, which settles insert the same way the production Esc does.
local function change(keys, text)
  typed = text
  vim.fn.feedkeys(tc(keys .. '<F14>'), 'x')
  vim.wait(50)  -- let the leave-side scheduled capture run
end

local function dot() vim.fn.feedkeys('.', 'x') end
local function keys(s) vim.fn.feedkeys(tc(s), 'x') end
local function scene(new_lines)
  vim.api.nvim_buf_set_lines(0, 0, -1, false, new_lines)
  vim.api.nvim_win_set_cursor(0, { 1, 0 })
  vim.wait(20)
end
local function line(n) return vim.api.nvim_buf_get_lines(0, n - 1, n, false)[1] end
local function all() return table.concat(vim.api.nvim_buf_get_lines(0, 0, -1, false), '|') end

-- cgn: the reported bug - '.' used to delete the next match, inserting nothing
vim.cmd('silent /target')
vim.api.nvim_win_set_cursor(0, { 1, 0 })
change('cgn', 'HIT')
t.eq(line(1), 'alpha HIT omega', 'cgn change applied')
keys('n')
dot()
t.eq(line(2), 'beta  HIT gamma', 'first . repeats the full change')
keys('n')
dot()
t.eq(line(3), 'delta HIT sigma', 'second . repeats the full change')

-- cw with motions before it: 'w' and 'j0' must not pollute the change keys
scene({ 'one two three', 'four five six' })
keys('w')
change('cw', 'TWO')
t.eq(line(1), 'one TWO three', 'cw change applied')
keys('j0')
dot()
t.eq(line(2), 'TWO five six', '. repeats cw, not the motions before it')

-- dd never enters insert: native dot owns it
scene({ 'l1', 'l2', 'l3' })
keys('dd')
t.eq(line(1), 'l2', 'dd applied')
dot()
t.eq(line(1), 'l3', 'native . repeats dd')

-- a native change after an insert-change takes redo back
scene({ 'one two', 'three four', 'five six' })
change('cw', 'ONE')
keys('j')
keys('dd')
dot()
t.eq(all(), 'ONE two', 'dd then . deletes again, no stale insert-change replay')

-- multi-line text through o
scene({ 'a', 'b' })
change('o', 'x\ny')
t.eq(all(), 'a|x|y|b', 'o with two lines applied')
dot()
t.eq(all(), 'a|x|y|x|y|b', '. repeats the multi-line insert')

-- dot with no recorded change is a native no-op
scene({ 'untouched' })
dot()
t.eq(line(1), 'untouched', 'dot with no change does nothing')

print('dot_repeat_tests: OK')
