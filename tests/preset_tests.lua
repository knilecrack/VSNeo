-- vim.g.vsneo_preset / :VSNeoPreset: a whole look under one name. Presets
-- are read through, never written into vim.g, so switching leaves nothing
-- behind. A preset from the rc is a base the user's own settings override;
-- one chosen live with :VSNeoPreset wins over them until none or a :source
-- of the rc.
-- vsneo_cursor_animation: [2] trail ms, [3] trail permille, [5] effects.
-- vsneo_cursor_style: [2] enabled, [9] blinking, [10] normal color, [15] cmdline
--   color, [16] glow, [17] mode line.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/preset.lua' })

local function wire()
  return t.report(h, 'vsneo_cursor_animation'), t.report(h, 'vsneo_cursor_style')
end

local function run(cmd)
  t.clear(h)
  vim.cmd(cmd)
  return wire()
end

local a, s = wire()
t.eq(a[2], 130, 'no preset: default trail')
t.eq(a[5], '', 'no preset: no effects')
t.eq(s[2], 0, "no preset: Visual Studio's caret")

a, s = run('VSNeoPreset blade_runner')
t.eq(vim.g.vsneo_preset, 'blade_runner', 'the command sets vim.g.vsneo_preset')
t.eq(a[2], 160, 'blade runner trail length')
t.eq(a[3], 850, 'blade runner trail size')
t.eq(a[5], 'scanline,torpedo,flicker,sparks', 'blade runner effects')
t.eq(s[2], 1, 'blade runner turns the custom cursor on')
t.eq(s[9], 'expand', 'blade runner blinking')
t.eq(s[10], 0xFF6C11, 'blade runner normal color')
t.eq(s[15], 0xFF6C11, 'the command line follows normal')
t.eq(s[16], 12, 'blade runner glow')
t.eq(s[17], 1, 'blade runner mode line')

-- Names are forgiving about case, spaces and dashes.
a, s = run('VSNeoPreset Cyberpunk2077')
t.eq(s[10], 0xFCEE0A, 'cyberpunk yellow, case-insensitive name')
t.eq(s[16], 10, 'cyberpunk glow radius')
a = run('VSNeoPreset blade-runner')
t.eq(a[2], 160, 'dashes read as underscores')

-- A live preset wins over the user's own settings: trying one needs no rc
-- edit. Look settings it leaves out are defaults, not the rc's values.
vim.g.vsneo_cursor_vfx_mode = 'railgun'
vim.g.vsneo_cursor_color = '#123456'
vim.g.vsneo_cursor_vfx_particle_curl = 2.0
vim.g.vsneo_beacon_width = 25
a, s = run('VSNeoPreset matrix')
t.eq(a[5], 'matrix,sparks,flicker', "a live preset's effects win over the user's")
t.eq(s[10], 0x00FF41, "a live preset's colors win over the user's")
t.eq(a[12], 1000, 'a look key the preset leaves out (curl) is the default, not the rc value')
t.eq(a[18], 25, 'settings outside the look (beacon width) stay the user\'s')

-- none: back to the rc's own look.
a, s = run('VSNeoPreset none')
t.eq(a[5], 'railgun', "none: the user's effects are back")
t.eq(s[10], 0x123456, "none: the user's color is back")
t.eq(a[12], 2000, "none: the user's curl is back")

-- A preset from the rc is a base: the rc's own settings still win over it.
vim.g.vsneo_preset = 'matrix'
a, s = run('doautocmd <nomodeline> SourcePost')
t.eq(a[5], 'railgun', "an rc preset: the user's effects win")
t.eq(s[9], 'phase', "an rc preset: the preset fills what the user left unset")
vim.g.vsneo_preset = nil

-- Re-sourcing the rc gives the rc's look back after a live switch.
run('VSNeoPreset cyberpunk2077')
local rc = vim.fn.expand('~/.vsneorc.lua')
vim.fn.mkdir(vim.fn.fnamemodify(rc, ':h'), 'p')
vim.fn.writefile({ '-- preset_tests' }, rc)
local sourced = pcall(vim.cmd, 'source ' .. vim.fn.fnameescape(rc))
os.remove(rc)   -- before any assertion: later suites share this HOME
t.expect(sourced, 'the rc sources')
a = wire()
t.eq(a[5], 'railgun', ':source of the rc ends the live preset')
local other = vim.fn.tempname() .. '.vim'
vim.fn.writefile({ '" not the rc' }, other)
run('VSNeoPreset cyberpunk2077')
a = run('source ' .. vim.fn.fnameescape(other))
t.eq(a[5], 'glitch,circuit,scanline,sparks', 'sourcing some other file keeps the live preset')
run('VSNeoPreset none')

vim.g.vsneo_cursor_vfx_mode = nil
vim.g.vsneo_cursor_color = nil
vim.g.vsneo_cursor_vfx_particle_curl = nil
vim.g.vsneo_beacon_width = nil

-- none: back to plain defaults, nothing left behind.
a, s = run('VSNeoPreset none')
t.eq(vim.g.vsneo_preset, nil, 'none clears vim.g.vsneo_preset')
t.eq(a[2], 130, 'none: default trail')
t.eq(a[5], '', 'none: no effects')
t.eq(s[2], 0, "none: Visual Studio's caret again")
t.eq(s[10], -1, 'none: no color')
t.eq(vim.g.vsneo_cursor_color, nil, 'no preset value was ever written into vim.g')

-- From the rc: vim.g.vsneo_preset applies on :source like any setting.
local path = vim.fn.tempname() .. '.vim'
vim.fn.writefile({ "let g:vsneo_preset = 'matrix'" }, path)
a, s = run('source ' .. vim.fn.fnameescape(path))
t.eq(a[5], 'matrix,sparks,flicker', 'rc preset applies on :source')
t.eq(s[13], 0x2A7FFF, 'matrix visual is the blue pill')

-- Unknown names report and change nothing.
local ok = pcall(vim.cmd, 'VSNeoPreset tron')
t.expect(ok, 'an unknown name reports instead of throwing')
t.eq(vim.g.vsneo_preset, 'matrix', 'an unknown name changes nothing')

-- An unknown name in the rc is just no preset.
local unknown = vim.fn.tempname() .. '.vim'
vim.fn.writefile({ "let g:vsneo_preset = 'tron'" }, unknown)
a = run('source ' .. vim.fn.fnameescape(unknown))
t.eq(a[5], '', 'an unknown rc preset is no preset')
t.expect(pcall(vim.cmd, 'VSNeoPreset'), 'listing with an unknown rc name does not throw')

-- Do not disturb still wins over a preset.
vim.g.vsneo_preset = 'cyberpunk2077'
a, s = run('VSNeoDnd on')
t.eq(a[2], 0, 'dnd over a preset: no trail')
t.eq(a[5], '', 'dnd over a preset: no effects')
t.eq(s[2], 0, "dnd over a preset: Visual Studio's caret")
a, s = run('VSNeoDnd off')
t.eq(a[5], 'glitch,circuit,scanline,sparks', 'dnd off: the preset is back')

-- Completion offers the presets and none.
local completions = vim.fn.getcompletion('VSNeoPreset ', 'cmdline')
t.expect(vim.tbl_contains(completions, 'blade_runner'), 'completion lists blade_runner')
t.expect(vim.tbl_contains(completions, 'none'), 'completion lists none')
t.eq(#vim.fn.getcompletion('VSNeoPreset ma', 'cmdline'), 1, 'completion filters by prefix')

print('preset_tests: ok')
