-- vsneo.buffer_hash(buf, known_tick): the mirror's drift check
-- (BufferMirror.Verify). { sha256 of the lines joined with '\n', line count,
-- changedtick }; when the buffer is still at known_tick, { '', count, tick }
-- without hashing - the settled pass that costs neither side a file read.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/buffer_hash.lua', lines = { 'alpha', 'béta 🎉', '', 'gamma' } })
local buf = vim.api.nvim_get_current_buf()

local r = vsneo.buffer_hash(buf)
t.eq(r[1], vim.fn.sha256('alpha\nbéta 🎉\n\ngamma'), "hash of the lines joined with '\\n'")
t.eq(r[2], 4, 'line count')
t.eq(r[3], vim.api.nvim_buf_get_changedtick(buf), 'changedtick')

-- No known tick (an old caller, or no agreement yet): always hashes.
t.eq(vsneo.buffer_hash(buf, -1)[1], r[1], '-1 hashes')

-- Still at the agreed tick: no hash.
local same = vsneo.buffer_hash(buf, r[3])
t.eq(same[1], '', 'unchanged since the agreement: empty hash, nothing read')
t.eq(same[2], 4, 'line count still reported')
t.eq(same[3], r[3], 'tick still reported')

-- Any edit moves the tick, and the full answer comes back.
vim.api.nvim_buf_set_lines(buf, 0, 1, false, { 'ALPHA' })
local moved = vsneo.buffer_hash(buf, r[3])
t.expect(moved[3] > r[3], 'an edit moves the tick')
t.eq(moved[1], vim.fn.sha256('ALPHA\nbéta 🎉\n\ngamma'), 'moved: hashed again')

print('buffer_hash_tests: ok')
