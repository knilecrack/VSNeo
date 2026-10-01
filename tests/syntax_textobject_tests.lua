-- af/if/ac/ic and ]m [m ]M [M ]] [[: the companion asks Visual Studio with
-- an rpcrequest ('vsneo_syntax', path, row, col, op, count) and selects or
-- moves to the answer [startRow, startByte, endRow, endByteInclusive,
-- linewise]. Visual Studio is faked here: the syntax itself is covered by the
-- .NET SyntaxTargetsTests; this covers the request and what the answer does.
-- Plus % across #region and #if arms, which is nvim's matchit with the C#
-- ftplugin, no Visual Studio involved.

local t = dofile('tests/helper.lua')
t.setup({ name = 'C:/test/Widget.cs' })

local lines = {}
for i = 1, 30 do lines[i] = 'line ' .. i end
vim.api.nvim_buf_set_lines(0, 0, -1, false, lines)

local requests = {}
local answer = nil
vim.rpcrequest = function(_, method, path, row, col, op, count)
  requests[#requests + 1] = { method = method, path = path, row = row, col = col, op = op, count = count }
  if type(answer) == 'function' then return answer(op, count) end
  return answer
end

local function feed(k)
  vim.api.nvim_feedkeys(vim.api.nvim_replace_termcodes(k, true, false, true), 'x', false)
end

-- The request carries the buffer, nvim's cursor, the operation and the count.
answer = { 10, 0, 14, 4, true }
vim.api.nvim_win_set_cursor(0, { 12, 3 })
feed('vaf')
local r = requests[#requests]
t.eq(r.method, 'vsneo_syntax', 'the request method')
t.eq(r.path, 'C:/test/Widget.cs', 'the request names the buffer')
t.eq(r.row, 12, 'the request carries the cursor row')
t.eq(r.col, 3, 'and the byte column')
t.eq(r.op, 'function_outer', 'af asks for function_outer')
t.eq(r.count, 1, 'count 1 by default')
t.eq(vim.api.nvim_get_mode().mode, 'V', 'a linewise answer selects linewise')
t.eq(vim.fn.line('v'), 10, 'from the start row')
t.eq(vim.fn.line('.'), 14, 'to the end row')
feed('<Esc>')

-- A count goes along, and the operator applies to the answer's lines.
vim.api.nvim_win_set_cursor(0, { 12, 0 })
feed('d2af')
t.eq(requests[#requests].count, 2, 'd2af asks for the second enclosing function')
t.eq(vim.api.nvim_buf_line_count(0), 25, 'daf deleted lines 10-14')
t.eq(vim.api.nvim_buf_get_lines(0, 9, 10, false)[1], 'line 15', 'the line after moved up')
vim.api.nvim_buf_set_lines(0, 0, -1, false, lines)

-- A charwise answer (an expression body, a lambda) selects characters.
answer = { 5, 2, 5, 4, false }
vim.api.nvim_win_set_cursor(0, { 5, 3 })
feed('cifX<Esc>')
t.eq(requests[#requests].op, 'function_inner', 'if asks for function_inner')
t.eq(vim.api.nvim_buf_get_lines(0, 4, 5, false)[1], 'liX5', 'cif replaced bytes 2-4 of line 5')
vim.api.nvim_buf_set_lines(0, 0, -1, false, lines)

-- ac / ic.
answer = { 3, 0, 20, 6, true }
vim.api.nvim_win_set_cursor(0, { 8, 0 })
feed('vic')
t.eq(requests[#requests].op, 'class_inner', 'ic asks for class_inner')
feed('<Esc>')
feed('vac')
t.eq(requests[#requests].op, 'class_outer', 'ac asks for class_outer')
feed('<Esc>')

-- No target: nothing selected, nothing deleted.
answer = vim.NIL
vim.api.nvim_win_set_cursor(0, { 8, 0 })
feed('daf')
t.eq(vim.api.nvim_buf_line_count(0), 30, 'no answer: daf deletes nothing')

-- Motions: the cursor lands on the answer, with a jumplist entry.
answer = function(op, count) return { 20 + count, 4, 20 + count, 4, false } end
vim.api.nvim_win_set_cursor(0, { 2, 0 })
feed(']m')
t.eq(requests[#requests].op, 'function_next_start', ']m asks for the next method start')
t.eq(vim.fn.line('.'), 21, ']m lands on the answer row')
t.eq(vim.fn.col('.'), 5, 'and its byte column')
feed("''")
t.eq(vim.fn.line('.'), 2, "'' comes back: ]m set a jumplist entry")
feed('3]m')
t.eq(requests[#requests].count, 3, '3]m sends the count')
t.eq(vim.fn.line('.'), 23, '3]m lands on the third')
for _, m in ipairs({ { '[m', 'function_prev_start' }, { ']M', 'function_next_end' },
                     { '[M', 'function_prev_end' }, { ']]', 'class_next_start' }, { '[[', 'class_prev_start' } }) do
  feed(m[1])
  t.eq(requests[#requests].op, m[2], m[1] .. ' asks for ' .. m[2])
end

-- Under an operator a motion is linewise: d]m takes whole lines.
vim.api.nvim_buf_set_lines(0, 0, -1, false, lines)
answer = { 8, 4, 8, 4, false }
vim.api.nvim_win_set_cursor(0, { 5, 2 })
feed('d]m')
t.eq(vim.api.nvim_buf_line_count(0), 26, 'd]m deleted lines 5-8 whole')
vim.api.nvim_buf_set_lines(0, 0, -1, false, lines)

-- A failed request warns and does nothing (no hang, no error).
vim.rpcrequest = function() error('VSNeo did not answer vsneo_syntax in time') end
vim.api.nvim_win_set_cursor(0, { 4, 0 })
feed('daf')
t.eq(vim.api.nvim_buf_line_count(0), 30, 'a failed request deletes nothing')
feed(']m')
t.eq(vim.fn.line('.'), 4, 'and moves nothing')

-- % across #region and #if arms: matchit with the C# ftplugin.
vim.cmd('packadd matchit')
vim.cmd('filetype plugin on')
vim.api.nvim_buf_set_lines(0, 0, -1, false, {
  'class C', '{', '    #region Fields', '    int a;', '    #if DEBUG', '    int b;', '    #elif TRACE',
  '    int c;', '    #else', '    int d;', '    #endif', '    #endregion', '}' })
vim.bo.filetype = 'cs'
local jumps = { { 3, 12 }, { 12, 3 }, { 5, 7 }, { 7, 9 }, { 9, 11 }, { 11, 5 } }
for _, j in ipairs(jumps) do
  vim.api.nvim_win_set_cursor(0, { j[1], 4 })
  feed('%')
  t.eq(vim.fn.line('.'), j[2], ('%% from line %d'):format(j[1]))
end

print('syntax_textobject_tests: ALL OK')
