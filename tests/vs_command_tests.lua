local t = dofile('tests/helper.lua')
local h = t.setup()

vsneo._set_vs_commands({ 'Edit.GoToDefinition', 'Debug.Start', 'Edit.FormatDocument' })
t.eq(table.concat(vim.fn.getcompletion('Vsc Edit.', 'cmdline'), '|'),
  'Edit.GoToDefinition|Edit.FormatDocument', ':Vsc completes the pushed command catalog')
t.eq(table.concat(vim.fn.getcompletion('Vsc Debug.', 'cmdline'), '|'),
  'Debug.Start', ':Vsc completion respects the prefix')

t.clear(h)
vim.cmd('Vsclist debug')
local list = t.report(h, 'vsneo_seeky')
t.eq(list[2], 'vscommands', ':Vsclist opens the VS command picker')
t.eq(list[3], 'debug', ':Vsclist pre-fills its filter')

t.clear(h)
vim.cmd('Seeky vscommands format')
list = t.report(h, 'vsneo_seeky')
t.eq(list[2], 'vscommands', ':Seeky exposes the VS command mode')
t.eq(list[3], 'format', ':Seeky forwards the query')
t.expect(vim.fn.maparg('vsclist', 'c', true) ~= '', 'lowercase :vsclist abbreviation is registered')

t.clear(h)
vim.cmd('VSNeoToggle')
t.eq(t.report(h, 'vsneo_toggle')[2], '', 'bare toggle has no explicit state')
vim.cmd('VSNeoToggle on')
t.eq(t.report(h, 'vsneo_toggle')[2], 'on', 'on is forwarded')
vim.cmd('VSNeoToggle OFF')
t.eq(t.report(h, 'vsneo_toggle')[2], 'off', 'off is normalized')

t.clear(h)
vim.cmd('VSNeoToggle invalid')
t.eq(t.report(h, 'vsneo_toggle'), nil, 'invalid state is rejected locally')
t.eq(table.concat(vim.fn.getcompletion('VSNeoToggle ', 'cmdline'), '|'), 'on|off', 'toggle states complete')

print('vs_command_tests: ALL OK')
