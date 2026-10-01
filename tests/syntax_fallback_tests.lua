-- When Visual Studio answers "no_tree" (no Roslyn document: C++, C, text),
-- the text objects and motions fall back: nvim's treesitter when it has a
-- parser for the language (it ships C), then Visual Studio's outlining
-- regions for the text objects, then nvim's built-in key for the motions.

local t = dofile('tests/helper.lua')
t.setup({ name = 'C:/test/fallback.c' })

local asked = 0
vim.rpcrequest = function() asked = asked + 1; return 'no_tree' end

local function feed(k)
  vim.api.nvim_feedkeys(vim.api.nvim_replace_termcodes(k, true, false, true), 'x', false)
end

local function selection()
  local s, e = vim.fn.getpos('v'), vim.fn.getpos('.')
  if s[2] > e[2] or (s[2] == e[2] and s[3] > e[3]) then s, e = e, s end
  return s[2], s[3] - 1, e[2], e[3] - 1, vim.api.nvim_get_mode().mode
end

-- ---- treesitter (C ships with nvim) ----------------------------------------
local c = {
  'struct point {',          -- 1
  '    int x;',              -- 2
  '    int y;',              -- 3
  '};',                      -- 4
  '',                        -- 5
  'int add(int a, int b)',   -- 6
  '{',                       -- 7
  '    int s = a + b;',      -- 8
  '    return s;',           -- 9
  '}',                       -- 10
  '',                        -- 11
  'int twice(int x)',        -- 12
  '{',                       -- 13
  '    return x * 2;',       -- 14
  '}',                       -- 15
}
vim.api.nvim_buf_set_lines(0, 0, -1, false, c)
vim.bo.filetype = 'c'

vim.api.nvim_win_set_cursor(0, { 8, 6 })
feed('vaf')
local sr, _, er, _, mode = selection()
t.expect(asked > 0, 'Visual Studio was asked first')
t.eq(mode, 'V', 'treesitter af: the function owns its lines, so linewise')
t.eq(sr, 6, 'af starts at the signature')
t.eq(er, 10, 'and ends at the closing brace')
feed('<Esc>')

vim.api.nvim_win_set_cursor(0, { 9, 4 })
feed('vif')
sr, _, er, _, mode = selection()
t.eq(mode, 'V', 'if: the lines between the braces')
t.eq(sr, 8, 'if starts inside the body')
t.eq(er, 9, 'and ends before the closing brace')
feed('<Esc>')

vim.api.nvim_win_set_cursor(0, { 2, 4 })
feed('vac')
sr, _, er = selection()
t.eq(sr, 1, 'ac: the struct from its first line')
t.eq(er, 4, 'to its closing line')
feed('<Esc>')
feed('vic')
sr, _, er = selection()
t.eq(sr, 2, 'ic: the members')
t.eq(er, 3, 'only')
feed('<Esc>')

vim.api.nvim_win_set_cursor(0, { 1, 0 })
feed(']m')
t.eq(vim.fn.line('.'), 6, 'treesitter ]m: next function')
feed(']m')
t.eq(vim.fn.line('.'), 12, ']m again')
feed('[m')
t.eq(vim.fn.line('.'), 6, '[m back')
feed(']M')
t.eq(vim.fn.line('.'), 10, ']M: the function end')
vim.api.nvim_win_set_cursor(0, { 14, 0 })
feed('[[')
t.eq(vim.fn.line('.'), 1, 'treesitter [[: the previous type')
vim.api.nvim_win_set_cursor(0, { 14, 0 })
feed(']m')
t.eq(vim.fn.line('.'), 14, 'no function after: the motion stays, no native fallback')

-- ---- outlining regions (no parser for this language) ------------------------
vim.api.nvim_buf_set_name(0, 'C:/test/fallback.txt')
vim.bo.filetype = 'text'
local lines = {}
for i = 1, 30 do lines[i] = 'line ' .. i end
lines[11] = '{'
vim.api.nvim_buf_set_lines(0, 0, -1, false, lines)
vsneo.folds_set('C:/test/fallback.txt', {})
vsneo.folds_set('C:/test/fallback.txt', { 5, 25, false, 10, 20, false })

vim.api.nvim_win_set_cursor(0, { 15, 0 })
feed('vaf')
sr, _, er, _, mode = selection()
t.eq(mode, 'V', 'region af: linewise')
t.eq(sr, 10, 'the innermost region')
t.eq(er, 20, 'whole')
feed('<Esc>')
feed('v2af')
sr, _, er = selection()
t.eq(sr, 5, 'a count goes outward')
t.eq(er, 25, 'to the enclosing region')
feed('<Esc>')
vim.api.nvim_win_set_cursor(0, { 15, 0 })
feed('vif')
sr, _, er = selection()
t.eq(sr, 12, 'region if: skips the header and a lone {')
t.eq(er, 19, 'and the closing line')
feed('<Esc>')

vim.api.nvim_win_set_cursor(0, { 2, 0 })
feed('daf')
t.eq(vim.api.nvim_buf_line_count(0), 30, 'outside every region: nothing')

-- ---- the built-in motion (no tree, no parser) -------------------------------
local raw = { 'a', '{', 'b', 'c', '{', 'd', '}' }
vim.api.nvim_buf_set_lines(0, 0, -1, false, raw)
vim.api.nvim_win_set_cursor(0, { 1, 0 })
feed(']]')
t.eq(vim.fn.line('.'), 2, 'no tree, no parser: ]] is nvim\'s own (next { in column 0)')
feed(']]')
t.eq(vim.fn.line('.'), 5, ']] again')
vim.api.nvim_win_set_cursor(0, { 1, 0 })
feed('2]]')
t.eq(vim.fn.line('.'), 5, 'the count goes to the built-in key')

print('syntax_fallback_tests: ALL OK')
