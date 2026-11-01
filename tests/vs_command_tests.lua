local t = dofile('tests/helper.lua')
local h = t.setup()

vsneo._set_vs_commands({ 'Edit.GoToDefinition', 'Debug.Start', 'Edit.FormatDocument' })
t.eq(table.concat(vim.fn.getcompletion('Vsc Edit.', 'cmdline'), '|'),
  'Edit.GoToDefinition|Edit.FormatDocument', ':Vsc completes the pushed command catalog')
t.eq(table.concat(vim.fn.getcompletion('Vsc Debug.', 'cmdline'), '|'),
  'Debug.Start', ':Vsc completion respects the prefix')
t.eq(table.concat(vim.fn.getcompletion('Vsc format', 'cmdline'), '|'),
  'Edit.FormatDocument', ':Vsc matches any part of the name, ignoring case')
vsneo._set_vs_commands({ 'Debug.StartWithoutDebugging', 'Build.Start', 'Debug.Start' })
t.eq(table.concat(vim.fn.getcompletion('Vsc debug.start', 'cmdline'), '|'),
  'Debug.StartWithoutDebugging|Debug.Start', 'names that start with it come first')
t.eq(table.concat(vim.fn.getcompletion('Vsc start', 'cmdline'), '|'),
  'Debug.StartWithoutDebugging|Build.Start|Debug.Start', 'then the rest, in catalog order')
t.eq(#vim.fn.getcompletion('Vsc Debug.Start ', 'cmdline'), 0, 'the argument after the name is not completed')
t.eq(vim.fn.getcompletion('silent Vsc build', 'cmdline')[1], 'Build.Start', 'after a command modifier')
vsneo._set_vs_commands({ 'Edit.GoToDefinition', 'Debug.Start', 'Edit.FormatDocument' })

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
