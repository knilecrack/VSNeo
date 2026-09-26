-- vsneo_cursor_style: VSNeo's own cursor. Off until the rc opts in; a string
-- sets the normal-mode shape, a table any mode; cmdline follows normal.
-- Wire: [enabled, normal, insert, replace, visual, operator, cmdline, blinking].

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/cursor_style.lua' })

local r = t.report(h, 'vsneo_cursor_style')
t.expect(r ~= nil, 'style should be pushed at load')
t.eq(r[2], 0, 'off by default: Visual Studio keeps its caret')
t.eq(r[3], 'block', 'normal default')
t.eq(r[4], 'line', 'insert default')
t.eq(r[5], 'underline', 'replace default')
t.eq(r[6], 'block', 'visual default')
t.eq(r[7], 'underline', 'operator-pending default')
t.eq(r[8], 'block', 'cmdline follows normal')
t.eq(r[9], 'blink', 'blinking default')

local function source(lines)
  local path = vim.fn.tempname() .. '.vim'
  vim.fn.writefile(lines, path)
  t.clear(h)
  vim.cmd('source ' .. vim.fn.fnameescape(path))
  return t.report(h, 'vsneo_cursor_style')
end

r = source({ "let g:vsneo_cursor_style = 'block-outline'" })
t.eq(r[2], 1, 'setting a style turns it on')
t.eq(r[3], 'block-outline', 'a string sets normal mode')
t.eq(r[4], 'line', 'insert keeps its default')
t.eq(r[8], 'block-outline', 'cmdline follows normal')

r = source({ "let g:vsneo_cursor_blinking = 'expand'" })
t.eq(r[9], 'expand', 'blinking passes through')

vim.g.vsneo_cursor_style = { insert = 'line-thin', cmdline = 'underline' }
r = source({ '" re-push' })
t.eq(r[3], 'block', 'table leaves unset modes at their defaults')
t.eq(r[4], 'line-thin', 'table sets insert')
t.eq(r[8], 'underline', 'table sets cmdline explicitly')

-- blinking alone also opts in
vim.g.vsneo_cursor_style = nil
r = source({ "let g:vsneo_cursor_blinking = 'smooth'" })
t.eq(r[2], 1, 'blinking alone turns the custom cursor on')

-- explicit off
vim.g.vsneo_cursor_blinking = nil
r = source({ 'let g:vsneo_cursor_style = 0' })
t.eq(r[2], 0, '0 turns it off')

print('cursor_style_tests: ALL OK')
