-- vsneo_esc_closes_popup: whether Escape with a completion list or
-- signature help open only closes the popup. The popup is Visual Studio's,
-- so the companion only carries the switch: [1] on, [0] off; off unless
-- set true/1.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/esc_closes_popup.lua' })

local r = t.report(h, 'vsneo_esc_closes_popup')
t.expect(r ~= nil, 'the switch is pushed at load')
t.eq(r[2], 0, 'off by default')

local function source(lines)
  local path = vim.fn.tempname() .. '.vim'
  vim.fn.writefile(lines, path)
  t.clear(h)
  vim.cmd('source ' .. vim.fn.fnameescape(path))
  return t.report(h, 'vsneo_esc_closes_popup')
end

r = source({ 'let g:vsneo_esc_closes_popup = v:true' })
t.expect(r ~= nil, ':source re-sends it')
t.eq(r[2], 1, 'true turns it on')

r = source({ 'let g:vsneo_esc_closes_popup = 0' })
t.eq(r[2], 0, '0 turns it off')

r = source({ 'let g:vsneo_esc_closes_popup = 1' })
t.eq(r[2], 1, '1 turns it on')

vim.g.vsneo_esc_closes_popup = nil
r = source({ '" re-push' })
t.eq(r[2], 0, 'unset is off')

print('esc_closes_popup_tests: ok')
