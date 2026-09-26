-- 'guicursor' push: the option is configuration for the extension-drawn
-- caret (GuiCursor.cs/CaretAdornment.cs), sent raw after the companion loads
-- and again on every OptionSet, so a ':set guicursor=...' applies live.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/guicursor.lua' })

-- Initial push carries the stock value: empty (VSNeo's default shape table).
local push = t.report(h, 'vsneo_guicursor')
t.expect(push ~= nil, 'companion should push vsneo_guicursor at load')
t.eq(push[2], '', 'stock guicursor should be empty')

t.clear(h)
vim.cmd('set guicursor=n:ver25,r-cr-o:hor20-blinkon0')
push = t.report(h, 'vsneo_guicursor')
t.expect(push ~= nil, ':set guicursor should push vsneo_guicursor')
t.eq(push[2], 'n:ver25,r-cr-o:hor20-blinkon0',
  'the option string should cross the wire raw')

t.clear(h)
vim.cmd('set guicursor=')
push = t.report(h, 'vsneo_guicursor')
t.eq(push[2], '', 'clearing the option should push an empty string')

print('guicursor_tests: OK')
