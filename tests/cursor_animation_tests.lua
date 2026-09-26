-- vsneo_cursor_animation: the cursor trail settings, pushed after the rc and
-- on every SourcePost, as integers (ms, per mille, 0/1).

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/cursor.lua' })

local r = t.report(h, 'vsneo_cursor_animation')
t.expect(r ~= nil, 'settings should be pushed at load')
t.eq(r[2], 130, 'default length is 130 ms')
t.eq(r[3], 800, 'default trail is 0.8')
t.eq(r[4], 1, 'insert-mode animation on by default')

local function source(lines)
  local path = vim.fn.tempname() .. '.vim'
  vim.fn.writefile(lines, path)
  t.clear(h)
  vim.cmd('source ' .. vim.fn.fnameescape(path))
  return t.report(h, 'vsneo_cursor_animation')
end

r = source({
  'let g:vsneo_cursor_animation_length = 0.2',
  'let g:vsneo_cursor_trail_size = 0.5',
  'let g:vsneo_cursor_animate_in_insert_mode = 0',
})
t.expect(r ~= nil, ':source should re-push the settings')
t.eq(r[2], 200, 'length in ms')
t.eq(r[3], 500, 'trail per mille')
t.eq(r[4], 0, 'vimscript 0 turns insert-mode animation off')

r = source({ 'let g:vsneo_cursor_animation_length = 0', 'let g:vsneo_cursor_trail_size = 7' })
t.eq(r[2], 0, 'zero length turns the trail off')
t.eq(r[3], 1000, 'trail size is clamped to 1')

vim.g.vsneo_cursor_animate_in_insert_mode = true
r = source({ 'let g:vsneo_cursor_animation_length = -1' })
t.eq(r[2], 0, 'negative length clamps to 0')
t.eq(r[4], 1, 'Lua true keeps insert-mode animation on')

print('cursor_animation_tests: ALL OK')
