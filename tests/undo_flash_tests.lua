-- vsneo_undo_flash: whether u / Ctrl+R flash what they changed. Undo is
-- Visual Studio's (the extension captures the changed spans itself), so the
-- companion only carries the switch: [1] on, [0] off; on unless set false/0.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/undo_flash.lua' })

local r = t.report(h, 'vsneo_undo_flash')
t.expect(r ~= nil, 'the switch is pushed at load')
t.eq(r[2], 1, 'on by default')

local function source(lines)
  local path = vim.fn.tempname() .. '.vim'
  vim.fn.writefile(lines, path)
  t.clear(h)
  vim.cmd('source ' .. vim.fn.fnameescape(path))
  return t.report(h, 'vsneo_undo_flash')
end

r = source({ 'let g:vsneo_undo_flash = 0' })
t.expect(r ~= nil, ':source re-sends it')
t.eq(r[2], 0, '0 turns it off')

r = source({ 'let g:vsneo_undo_flash = v:false' })
t.eq(r[2], 0, 'false turns it off')

r = source({ 'let g:vsneo_undo_flash = 1' })
t.eq(r[2], 1, '1 turns it back on')

vim.g.vsneo_undo_flash = nil
r = source({ '" re-push' })
t.eq(r[2], 1, 'unset is on')

print('undo_flash_tests: ok')
