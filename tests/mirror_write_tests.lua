-- vsneo.apply_spans / vsneo.set_all_lines: BufferMirror's write path, which
-- applies Visual Studio's edits and returns the changedtick in the same reply.

local t = dofile('tests/helper.lua')
t.setup({
  name = 'C:/test/mirror.lua',
  lines = { 'hello world', 'second line', 'héllo' },
})

local buf = vim.api.nvim_get_current_buf()
local function lines() return vim.api.nvim_buf_get_lines(buf, 0, -1, false) end

-- One span, a typed character: tick comes back and matches the buffer's.
local r = vsneo.apply_spans(buf, { { 0, 5, 0, 5, { '!' } } })
t.eq(lines()[1], 'hello! world', 'single span inserts')
t.eq(r[1], vim.api.nvim_buf_get_changedtick(buf), 'returns the current changedtick')
t.eq(r[2], 0, 'no failures')
t.eq(r[3], '', 'no error text')

-- Several spans, last-first as the extension sends them: earlier offsets
-- must stay valid.
vsneo.apply_spans(buf, {
  { 1, 7, 1, 11, { 'LINE' } },
  { 1, 0, 1, 6, { 'SECOND' } },
})
t.eq(lines()[2], 'SECOND LINE', 'reverse-ordered spans both land')

-- Multi-line replacement and deletion.
vsneo.apply_spans(buf, { { 0, 6, 0, 12, { '', 'split' } } })
t.eq(lines()[1], 'hello!', 'newline splits the line')
t.eq(lines()[2], 'split', 'second half on its own line')
vsneo.apply_spans(buf, { { 0, 6, 1, 0, { '' } } })
t.eq(lines()[1], 'hello!split', 'deleting the break joins the lines')

-- Byte columns: é is two bytes.
vsneo.apply_spans(buf, { { 2, 3, 2, 3, { 'X' } } })
t.eq(lines()[3], 'héXllo', 'byte columns, not char columns')

-- A bad span is reported but does not cost the good ones, and the tick
-- still comes back.
local before = vim.api.nvim_buf_get_changedtick(buf)
r = vsneo.apply_spans(buf, {
  { 99, 0, 99, 0, { 'nope' } },
  { 0, 0, 0, 0, { '>' } },
})
t.eq(lines()[1], '>hello!split', 'good span lands despite a bad one')
t.eq(r[2], 1, 'one failure counted')
t.expect(r[3] ~= '', 'first error text reported')
t.expect(r[1] > before, 'tick reflects the good span')
t.eq(r[1], vim.api.nvim_buf_get_changedtick(buf), 'tick is current')

-- Whole-buffer replace.
local tick = vsneo.set_all_lines(buf, { 'a', 'b' })
t.eq(#lines(), 2, 'set_all_lines replaces everything')
t.eq(lines()[2], 'b', 'content replaced')
t.eq(tick, vim.api.nvim_buf_get_changedtick(buf), 'set_all_lines returns the tick')

-- An invalid buffer raises (not partial, so the caller sees a failure).
t.expect(not pcall(vsneo.set_all_lines, 99999, { 'x' }), 'invalid buffer raises')

print('mirror_write_tests: ALL OK')
