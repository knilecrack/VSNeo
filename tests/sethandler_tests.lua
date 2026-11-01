local t = dofile('tests/helper.lua')
local h = t.setup()

t.clear(h)
vim.cmd('Sethandler <C-p> n:ide i:ide')
local report = t.report(h, 'vsneo_sethandler')
t.eq(report[2], '<C-p> n:ide i:ide', 'key and ownership assignments cross unchanged')

t.clear(h)
vim.cmd('Sethandler <F2> a:ide c:vim')
report = t.report(h, 'vsneo_sethandler')
t.eq(report[2], '<F2> a:ide c:vim', 'all-mode and command-line assignments work')
t.expect(vim.fn.maparg('sethandler', 'c', true) ~= '', 'lowercase :sethandler abbreviation is registered')

t.clear(h)
vim.cmd('Sethandler n:vim i:ide')
report = t.report(h, 'vsneo_sethandler')
t.eq(report[2], 'n:vim i:ide', 'mode-wide defaults need no key')

print('sethandler_tests: ALL OK')
