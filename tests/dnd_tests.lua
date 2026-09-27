-- vim.g.vsneo_dnd / :VSNeoDnd: do not disturb. Every animation and effect
-- goes off on the wire, the user's settings stay as they were, and turning
-- it off brings them back.
-- vsneo_cursor_animation: [2] length ms, [5] effects, [13] short ms,
--   [14] scroll ms, [16] beacon.
-- vsneo_cursor_style: [2] enabled, [16] glow, [17] mode line.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/dnd.lua' })

vim.g.vsneo_cursor_vfx_mode = { 'matrix', 'sparks' }
vim.g.vsneo_cursor_style = 'block-outline'
vim.g.vsneo_cursor_glow = true
vim.g.vsneo_mode_line = true


local function wire()
  return t.report(h, 'vsneo_cursor_animation'), t.report(h, 'vsneo_cursor_style')
end

local function source(lines)
  local path = vim.fn.tempname() .. '.vim'
  vim.fn.writefile(lines, path)
  t.clear(h)
  vim.cmd('source ' .. vim.fn.fnameescape(path))
end

source({ '" configured' })
local a, s = wire()
t.eq(a[2], 130, 'trail on before dnd')
t.eq(a[5], 'matrix,sparks', 'effects on before dnd')
t.eq(s[2], 1, 'custom cursor on before dnd')

-- The rc variable.
source({ 'let g:vsneo_dnd = 1' })
a, s = wire()
t.eq(a[2], 0, 'dnd: no trail')
t.eq(a[5], '', 'dnd: no effects')
t.eq(a[13], 0, 'dnd: no short-move animation')
t.eq(a[14], 0, 'dnd: no smooth scrolling')
t.eq(a[16], 0, 'dnd: no beacon')
t.eq(s[2], 0, "dnd: Visual Studio's own caret")
t.eq(s[16], 0, 'dnd: no glow')
t.eq(s[17], 0, 'dnd: no mode line')

-- The command flips it live and leaves the settings alone.
t.clear(h)
vim.cmd('VSNeoDnd')
a, s = wire()
t.eq(vim.g.vsneo_dnd, false, ':VSNeoDnd with no argument toggles off')
t.eq(a[2], 130, 'trail back')
t.eq(a[5], 'matrix,sparks', 'effects back')
t.eq(a[16], 1, 'beacon back')
t.eq(s[2], 1, 'custom cursor back')
t.eq(s[16], 12, 'glow back')
t.eq(s[17], 1, 'mode line back')

t.clear(h)
vim.cmd('VSNeoDnd on')
a = wire()
t.eq(a[2], 0, ':VSNeoDnd on')
t.clear(h)
vim.cmd('VSNeoDnd on')
a = wire()
t.eq(a[2], 0, ':VSNeoDnd on twice stays on')
t.clear(h)
vim.cmd('VSNeoDnd off')
a = wire()
t.eq(a[2], 130, ':VSNeoDnd off')

local ok = pcall(vim.cmd, 'VSNeoDnd maybe')
t.expect(ok, 'a bad argument reports instead of throwing')
t.eq(vim.g.vsneo_dnd, false, 'a bad argument changes nothing')

print('dnd_tests: ok')
