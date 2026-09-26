-- vsneo_cursor_animation: the cursor trail settings, pushed after the rc and
-- on every SourcePost, as integers (ms, per mille, 0/1).

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/cursor.lua' })

local r = t.report(h, 'vsneo_cursor_animation')
t.expect(r ~= nil, 'settings should be pushed at load')
t.eq(r[2], 130, 'default length is 130 ms')
t.eq(r[3], 800, 'default trail is 0.8')
t.eq(r[4], 1, 'insert-mode animation on by default')
t.eq(r[5], '', 'vfx off by default, as in Neovide')
t.eq(r[6], 200, 'default vfx opacity (0..255)')
t.eq(r[7], 500, 'default particle lifetime 0.5 s')
t.eq(r[8], 200, 'default highlight lifetime 0.2 s')
t.eq(r[9], 700, 'default density 0.7')
t.eq(r[10], 10000, 'default speed 10.0')
t.eq(r[11], 1500, 'default phase 1.5')
t.eq(r[12], 1000, 'default curl 1.0')
t.eq(r[13], 40, 'default short-move length 0.04 s (Neovide)')

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

-- VFX: a string mode, a list of modes, and the numbers.
r = source({
  "let g:vsneo_cursor_vfx_mode = 'railgun'",
  'let g:vsneo_cursor_vfx_opacity = 300',
  'let g:vsneo_cursor_vfx_particle_lifetime = 1.2',
  'let g:vsneo_cursor_vfx_particle_density = 7',
})
t.eq(r[5], 'railgun', 'single vfx mode passes through')
t.eq(r[6], 255, 'opacity clamps to 255')
t.eq(r[7], 1200, 'lifetime in ms')
t.eq(r[9], 7000, 'density per mille')

r = source({ "let g:vsneo_cursor_vfx_mode = ['pixiedust', 'sonicboom']" })
t.eq(r[5], 'pixiedust,sonicboom', 'a list of modes is comma-joined')

r = source({ 'let g:vsneo_cursor_short_animation_length = 0.02' })
t.eq(r[13], 20, 'short-move length in ms')
r = source({ 'let g:vsneo_cursor_short_animation_length = -1' })
t.eq(r[13], 0, 'negative short-move length clamps to 0')

print('cursor_animation_tests: ALL OK')
