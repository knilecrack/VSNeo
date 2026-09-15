-- Foreign-buffer doors: vsneo.find_buffer (the mirror's adoption lookup)
-- and the :Buffer command resolution.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/main.lua' })

local function open_actions()
  local out = {}
  for _, n in ipairs(t.reports(h, 'vsneo_action')) do
    if n[2] == 'File.OpenFile' then out[#out + 1] = n[3] end
  end
  return out
end

-- find_buffer: the current buffer, normalization, unknown paths
t.eq(vsneo.find_buffer('C:/test/main.lua'), vim.api.nvim_get_current_buf(),
  'find_buffer should find the current buffer')
t.eq(vsneo.find_buffer('c:\\TEST\\MAIN.lua'), vim.api.nvim_get_current_buf(),
  'find_buffer should normalize slashes and case')
t.eq(vsneo.find_buffer('C:/test/nope.lua'), 0, 'unknown path should be 0')

local other = vim.api.nvim_create_buf(true, false)
vim.api.nvim_buf_set_name(other, 'C:/test/other.lua')
t.eq(vsneo.find_buffer('C:/test/other.lua'), other, 'find_buffer should find a background buffer')

-- :Buffer by name substring and by number
t.clear(h)
vim.cmd('Buffer other.lua')
actions = open_actions()
t.eq(#actions, 1, ':Buffer by name should send one File.OpenFile')
t.expect(actions[1]:lower():find('other.lua', 1, true) ~= nil,
  ':Buffer should open other.lua, got ' .. tostring(actions[1]))

t.clear(h)
vim.cmd('Buffer ' .. other)
actions = open_actions()
t.eq(#actions, 1, ':Buffer by number should send one File.OpenFile')

-- :Buffer with no match: an error message, no VS command. The error surfaces
-- through the message margin in real usage; in a script it raises, hence the
-- pcall.
t.clear(h)
pcall(vim.cmd, 'Buffer zzz-no-such-buffer')
t.eq(#open_actions(), 0, ':Buffer with no match must not open anything')

print('buffer_tests: ALL OK')
