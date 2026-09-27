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

-- Colors: wire slots 10..15 (normal..cmdline), glow at 16.
vim.g.vsneo_cursor_style = nil
r = source({ '" re-push' })
t.eq(r[10], -1, 'unset color is -1 (theme caret color)')
t.eq(r[16], 0, 'glow off by default')
t.eq(r[17], 0, 'mode line off by default')
t.eq(r[18], 120, 'mode line opacity 0.12')

r = source({ "let g:vsneo_cursor_color = '#FF2A6D'" })
t.eq(r[2], 1, 'a color alone turns the custom cursor on')
for i = 10, 15 do t.eq(r[i], 0xFF2A6D, 'a single color applies to every mode') end

vim.g.vsneo_cursor_color = { normal = '#05D9E8', insert = '#FF2A6D' }
r = source({ '" re-push' })
t.eq(r[10], 0x05D9E8, 'table: normal')
t.eq(r[11], 0xFF2A6D, 'table: insert')
t.eq(r[12], -1, 'table: unset replace uses the theme')
t.eq(r[15], 0x05D9E8, 'table: cmdline follows normal')

vim.api.nvim_set_hl(0, 'NeonCursor', { bg = '#39FF14' })
vim.g.vsneo_cursor_color = 'NeonCursor'
r = source({ '" re-push' })
t.eq(r[10], 0x39FF14, 'a highlight group name resolves to its bg')

vim.g.vsneo_cursor_color = '#nothex'
r = source({ '" re-push' })
t.eq(r[10], -1, 'a malformed hex falls back to the theme')

vim.g.vsneo_cursor_glow = true
r = source({ '" re-push' })
t.eq(r[16], 12, 'glow = true is 12 px')
vim.g.vsneo_cursor_glow = 200
r = source({ '" re-push' })
t.eq(r[16], 60, 'glow is clamped')

-- A colorscheme change re-sends (highlight-group colors follow it).
vim.g.vsneo_cursor_color = 'NeonCursor'
t.clear(h)
vim.api.nvim_set_hl(0, 'NeonCursor', { bg = '#B967FF' })
vim.api.nvim_exec_autocmds('ColorScheme', {})
r = t.report(h, 'vsneo_cursor_style')
t.expect(r ~= nil, 'ColorScheme should re-push')
t.eq(r[10], 0xB967FF, 'the new highlight color is sent')

vim.g.vsneo_mode_line = true
vim.g.vsneo_mode_line_opacity = 0.2
r = source({ '" re-push' })
t.eq(r[17], 1, 'mode line on')
t.eq(r[18], 200, 'mode line opacity per mille')
r = source({ 'let g:vsneo_mode_line = 0', 'let g:vsneo_mode_line_opacity = 5' })
t.eq(r[17], 0, 'vimscript 0 turns the mode line off')
t.eq(r[18], 1000, 'opacity clamps to 1')

print('cursor_style_tests: ALL OK')
