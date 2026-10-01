-- vsneo.cmd_select and the visual-mode + / - defaults: the command goes out
-- as vsneo_select_action, and the extension hands the selection it leaves
-- back as visual mode (CursorSynchronizer.HandOverSelection).

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/expand.lua' })

vim.api.nvim_buf_set_lines(0, 0, -1, false, { 'int x = f(a, b);' })

local function feed(keys)
  t.clear(h)
  vim.api.nvim_feedkeys(vim.api.nvim_replace_termcodes(keys, true, false, true), 'x', false)
  return t.report(h, 'vsneo_select_action')
end

vim.api.nvim_win_set_cursor(0, { 1, 10 })
local r = feed('v+')
t.expect(r ~= nil, 'visual + sends a select action')
t.eq(r[2], 'Edit.ExpandSelection', '+ expands')
t.eq(vim.api.nvim_get_mode().mode, 'v', 'still in visual: the selection comes back from the extension')

r = feed('-')
t.eq(r[2], 'Edit.ContractSelection', '- contracts')
feed('<Esc>')

r = feed('+')
t.expect(r == nil, 'normal-mode + is untouched (a line motion)')

t.clear(h)
vsneo.cmd_select('Edit.SelectCurrentWord')
r = t.report(h, 'vsneo_select_action')
t.eq(r[2], 'Edit.SelectCurrentWord', 'cmd_select takes any command')

print('expand_selection_tests: ALL OK')
