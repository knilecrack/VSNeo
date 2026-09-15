-- Fold state must not bleed across buffers: when nvim moves its own window
-- (:b, gf, a file-mark jump), the agreed fold list belongs to the buffer
-- that was left. BufEnter resets it; Visual Studio re-pushes a full
-- folds_set for the document it shows. Without the reset, the stale list
-- reports phantom fold changes against the new buffer and its echo guard
-- swallows legitimate closes there.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/one.lua' })

-- buffer one: an agreed closed fold
vsneo.folds_set('C:/test/one.lua', { 10, 20, true })
t.eq(vim.fn.foldclosed(10), 10, 'scene: fold 10-20 closed in buffer one')
local buf1 = vim.api.nvim_get_current_buf()
local tick1 = vim.api.nvim_buf_get_changedtick(buf1)

-- buffer two, entered the way nvim moves its own window
local buf2 = vim.api.nvim_create_buf(true, false)
local lines = {}
for i = 1, 100 do lines[i] = 'two line ' .. i end
vim.api.nvim_buf_set_lines(buf2, 0, -1, false, lines)
vim.api.nvim_buf_set_name(buf2, 'C:/test/two.lua')
-- Align the changedticks so the stale list would pass detection's tick gate:
-- without the BufEnter reset, the phantom report below fires.
while vim.api.nvim_buf_get_changedtick(buf2) < tick1 do
  vim.api.nvim_buf_set_lines(buf2, -1, -1, false, { 'pad' })
end

-- (a) no phantom report from buffer one's stale agreed state, neither from
-- the switch itself (BufEnter drives a push) nor from the first push after
t.clear(h)
vim.api.nvim_set_current_buf(buf2)
t.eq(vim.api.nvim_get_current_buf(), buf2, 'scene: switched to buffer two')
t.expect(t.report(h, 'vsneo_folds_changed') == nil,
  'stale agreed folds reported during the buffer switch')
vim.cmd('doautocmd CursorMoved')
t.expect(t.report(h, 'vsneo_folds_changed') == nil,
  'stale agreed folds reported after the buffer switch')

-- (b) a legitimate close in buffer two is not swallowed by the stale echo
-- guard (buffer one's agreed list says 10-20 is already closed). Rebuild the
-- stale situation, this time with a changedtick mismatch so detection stays
-- silent and cannot clear the stale list before fold_closed runs.
vim.api.nvim_set_current_buf(buf1)
vsneo.folds_set('C:/test/one.lua', { 10, 20, true })
vim.api.nvim_set_current_buf(buf2)
vim.api.nvim_buf_set_lines(buf2, -1, -1, false, { 'tick bump' })
t.clear(h)
vim.cmd('doautocmd CursorMoved')
t.expect(t.report(h, 'vsneo_folds_changed') == nil,
  'tick-mismatched stale state reported after the switch')
vim.cmd('10,20fold')
vim.cmd('10foldopen!')   -- :fold creates the fold closed; open it first
t.expect(vim.fn.foldlevel(10) > 0 and vim.fn.foldclosed(10) == -1,
  'scene: fold 10-20 exists and is open in buffer two')
vsneo.fold_closed('C:/test/two.lua', 10, 20)
t.eq(vim.fn.foldclosed(10), 10,
  'fold_closed swallowed by the stale echo guard after the buffer switch')

print('fold_buffer_switch_tests: ALL OK')
