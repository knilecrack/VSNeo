local function unless_runtime(fn)
  return function(args)
    if args.event == 'SourcePost' and runtime_dir ~= '' and args.file ~= '' then
      local file = vim.fs.normalize(args.file):lower()
      if file:sub(1, #runtime_dir) == runtime_dir then return end
    end
    fn()
  end
end

-- 'cmdheight' is read, not applied. ext_messages zeroes nvim's at attach, but
-- a later 'set cmdheight=2' sticks and takes rows from the window like a
-- status line does: the window then has N lines fewer than the view, and L,
-- M, <C-d> and the scroll sync all land N lines off. So nvim's stays 0 and
-- the value goes to the extension, where it sets how many lines a message
-- may have before it opens the pager (Vim's more prompt).
local cmdheight, sent_cmdheight = 0, nil
local function send_cmdheight()
  if cmdheight == sent_cmdheight then return end
  sent_cmdheight = cmdheight
  vim.rpcnotify(chan, 'vsneo_cmdheight', cmdheight)
end

local function reassert_forced()
  -- Set before the OptionSet below existed (the rc), or with :noautocmd.
  if vim.o.cmdheight ~= 0 then
    cmdheight = vim.o.cmdheight
    vim.o.cmdheight = 0
  end
  send_cmdheight()
  vim.o.wrap = false
  vim.o.sidescrolloff = 0
  vim.o.splitkeep = 'topline'
  vim.o.laststatus = 0
  vim.o.swapfile = false
  vim.o.inccommand = ''
  vim.o.backup = false
  vim.o.writebackup = false
  vim.opt.shortmess:append('A')
  vim.wo.foldmethod = 'manual'
  vim.wo.foldlevel = 99
end
reassert_forced()
vim.api.nvim_create_autocmd('SourcePost', {
  group = group,
  callback = unless_runtime(reassert_forced),
})
-- The two that size the window are also forced against a live ':set', which
-- fires no SourcePost.
vim.api.nvim_create_autocmd('OptionSet', {
  group = group,
  pattern = { 'cmdheight', 'laststatus' },
  callback = function(args)
    if args.match == 'cmdheight' then
      cmdheight = math.max(0, tonumber(vim.v.option_new) or 0)
    end
    vim.o.cmdheight = 0
    vim.o.laststatus = 0
    send_cmdheight()
  end,
})

------------------------------------------------------------------
-- Highlight groups as configuration
--
-- nvim renders nothing, but the extension draws search matches and the yank
-- flash as WPF adornments - and their colors come from here, so a ':hi Search
-- guibg=...' line in ~/.vsneorc really does change what Visual Studio shows.
-- Positional args with -1 for "unset": keeps the C# msgpack reader trivial.
-- Runs after the rc precisely so user definitions are the ones we read.
------------------------------------------------------------------

local function hl_bg(name)
  local ok, hl = pcall(vim.api.nvim_get_hl, 0, { name = name, link = false })
  if ok and hl and hl.bg then return hl.bg end
  return -1
end

-- The fourth value is vim.g.vsneo_search_count (on unless false/0): the
-- [3/17] chip Visual Studio draws at the end of the current match's line.
-- It rides this push because it belongs to the same drawing, and SourcePost
-- re-sends it so ':source' toggles it live.
local function send_highlights()
  local cur = hl_bg('CurSearch')
  if cur == -1 then cur = hl_bg('IncSearch') end
  local count = vim.g.vsneo_search_count
  local count_on = not (count == false or count == 0)
  vim.rpcnotify(chan, 'vsneo_highlights', hl_bg('Search'), cur, hl_bg('IncSearch'),
    count_on and 1 or 0)
end

send_highlights()
vim.api.nvim_create_autocmd({ 'ColorScheme', 'SourcePost' }, {
  group = group,
  callback = unless_runtime(send_highlights),   -- see unless_runtime above
})

-- vim.g.vsneo_undo_flash (on unless false/0): u and Ctrl+R flash what they
-- changed (UndoFlashAdornment). Undo is Visual Studio's, so the extension
-- finds the changed text itself; only the switch comes from here, re-sent on
-- SourcePost so ':source' toggles it live.
local function send_undo_flash()
  local v = vim.g.vsneo_undo_flash
  vim.rpcnotify(chan, 'vsneo_undo_flash', (v == false or v == 0) and 0 or 1)
end

send_undo_flash()
vim.api.nvim_create_autocmd('SourcePost', {
  group = group,
  callback = unless_runtime(send_undo_flash),
})

-- The Seeky picker's 'nvim' theme (Ctrl+T in the picker) and its prompt
-- normal mode. The palette is Telescope's highlight groups where the
-- colorscheme defines them, falling back to the float and core groups, so
-- the picker looks like a Telescope float in this colorscheme. Every value
-- is filled - a missing one would show the default theme's color through.
-- vim.g.vsneo_seeky_prompt_normal (off unless true/1): Escape leaves the
-- prompt for j/k, gg/G, i/a; a second Escape closes.
local function hl_attr(names, attr)
  for _, name in ipairs(names) do
    local ok, hl = pcall(vim.api.nvim_get_hl, 0, { name = name, link = false })
    if ok and hl and hl[attr] then return string.format('#%06x', hl[attr]) end
  end
end

local function seeky_palette()
  local bg = hl_attr({ 'TelescopeNormal', 'NormalFloat', 'Normal' }, 'bg')
  local fg = hl_attr({ 'TelescopeNormal', 'NormalFloat', 'Normal' }, 'fg')
  if not bg or not fg then return nil end   -- no colorscheme colors to borrow
  local sel = hl_attr({ 'TelescopeSelection', 'Visual', 'PmenuSel' }, 'bg') or bg
  local match_fg = hl_attr({ 'TelescopeMatching' }, 'fg')
  return {
    ['bg'] = bg,
    ['bg-alt'] = hl_attr({ 'TelescopePromptNormal', 'StatusLine', 'Pmenu' }, 'bg') or bg,
    ['bg-sel'] = sel,
    ['border'] = hl_attr({ 'TelescopeBorder', 'FloatBorder', 'WinSeparator' }, 'fg') or fg,
    ['fg'] = fg,
    ['fg-dim'] = hl_attr({ 'Comment', 'NonText' }, 'fg') or fg,
    ['accent'] = hl_attr({ 'TelescopePromptPrefix', 'TelescopeTitle', 'Title', 'Function' }, 'fg') or fg,
    ['hot'] = hl_attr({ 'TelescopeSelection', 'Visual' }, 'fg') or fg,
    ['amber'] = hl_attr({ 'DiagnosticWarn', 'WarningMsg' }, 'fg') or fg,
    ['green'] = hl_attr({ 'DiagnosticOk', 'String' }, 'fg') or fg,
    ['hl'] = hl_attr({ 'CursorLine' }, 'bg') or sel,
    -- TelescopeMatching colors the matched characters; without it, Search's
    -- chip does (its background behind its foreground).
    ['mhl-fg'] = match_fg or hl_attr({ 'Search' }, 'fg') or fg,
    ['mhl-bg'] = match_fg and bg or hl_attr({ 'Search' }, 'bg') or bg,
  }
end

local function send_seeky_config()
  local v = vim.g.vsneo_seeky_prompt_normal
  vim.rpcnotify(chan, 'vsneo_seeky_config', seeky_palette() or vim.NIL,
    (v == true or v == 1) and 1 or 0)
end

send_seeky_config()
vim.api.nvim_create_autocmd({ 'ColorScheme', 'SourcePost' }, {
  group = group,
  callback = unless_runtime(send_seeky_config),
})

-- vim.g.vsneo_esc_closes_popup (off unless true/1): with a completion list
-- or signature help open in insert mode, Escape only closes the popup and
-- insert mode stays; the next Escape leaves insert. Off, one Escape does
-- both. The popup lives in Visual Studio, so the decision is the
-- extension's (VsNeoCommandFilter.TryHandleEscape); this carries the
-- switch, re-sent on SourcePost so ':source' toggles it live.
local function send_esc_closes_popup()
  local v = vim.g.vsneo_esc_closes_popup
  vim.rpcnotify(chan, 'vsneo_esc_closes_popup', (v == true or v == 1) and 1 or 0)
end

send_esc_closes_popup()
vim.api.nvim_create_autocmd('SourcePost', {
  group = group,
  callback = unless_runtime(send_esc_closes_popup),
})

------------------------------------------------------------------
-- Vertical scroll padding
--
-- ViewportSynchronizer needs the effective window-local value to recognize
-- follow-scrolls and reproduce the padding with Visual Studio's laid-out lines.
------------------------------------------------------------------

local last_scrolloff
local function send_scrolloff()
  local value = math.max(0, vim.wo.scrolloff)
  if value == last_scrolloff then return end
  last_scrolloff = value
  vim.rpcnotify(chan, 'vsneo_scrolloff', value)
end

send_scrolloff()
vim.api.nvim_create_autocmd('OptionSet', {
  group = group,
  pattern = 'scrolloff',
  callback = send_scrolloff,
})
vim.api.nvim_create_autocmd('BufWinEnter', {
  group = group,
  callback = send_scrolloff,
})
vim.api.nvim_create_autocmd('SourcePost', {
  group = group,
  callback = unless_runtime(send_scrolloff),
})

------------------------------------------------------------------
-- Line number options
--
-- nvim's grid is never displayed, so 'number' and 'relativenumber'
-- render nothing here; they are configuration for the extension's
-- relative line number margin (RelativeLineNumberMargin.cs), which
-- reads them like Vim would. Pushed after the rc so user settings
-- are the ones we read, and again from OptionSet so ':set rnu' and
-- friends toggle the margin live. Both options are window-local;
-- the one-window model makes vim.wo the right read.
--
-- Window-local is also the trap: nvim remembers a window's options per
-- buffer and restores them when the buffer comes back, without
-- OptionSet. A buffer shown before the rc ran (the startup buffer, a
-- document attached early) returned with 'number' off, and the margin
-- drew 0 on the cursor line although the rc said number. There is one
-- margin setting, not one per buffer, so every buffer entry re-applies
-- the global (':set') values, and a :source re-sends.
------------------------------------------------------------------

local last_linenumbers
local function send_linenumbers()
  local nu, rnu = vim.wo.number and 1 or 0, vim.wo.relativenumber and 1 or 0
  local key = nu * 2 + rnu
  if key == last_linenumbers then return end
  last_linenumbers = key
  vim.rpcnotify(chan, 'vsneo_linenumbers', nu, rnu)
end

send_linenumbers()
vim.api.nvim_create_autocmd('OptionSet', {
  group = group,
  pattern = { 'number', 'relativenumber' },
  callback = send_linenumbers,
})
vim.api.nvim_create_autocmd('BufWinEnter', {
  group = group,
  callback = function()
    local win = vim.api.nvim_get_current_win()
    for _, name in ipairs({ 'number', 'relativenumber' }) do
      local global = vim.api.nvim_get_option_value(name, { scope = 'global' })
      if vim.wo[win][name] ~= global then
        vim.api.nvim_set_option_value(name, global, { scope = 'local', win = win })
      end
    end
    send_linenumbers()
  end,
})
vim.api.nvim_create_autocmd('SourcePost', {
  group = group,
  callback = unless_runtime(send_linenumbers),
})

------------------------------------------------------------------
-- Cursor trail (CursorTrailAdornment.cs)
--
-- Neovide's cursor animation settings, under their Neovide names with a
-- vsneo_ prefix, so a Neovide config ports by renaming:
--   vim.g.vsneo_cursor_animation_length       seconds, 0 turns it off (0.13)
--   vim.g.vsneo_cursor_short_animation_length moves of <= 2 columns on one
--                                             line - typing, h/l (0.04)
-- Smooth scrolling for nvim-driven scrolls (<C-d>, zz, G, ...), Neovide's:
--   vim.g.vsneo_scroll_animation_length       seconds, 0 = instant (0.3)
--   vim.g.vsneo_scroll_animation_far_lines    past one screen, only this many
--                                             lines at the end animate (1)
-- Jump beacon (beacon.nvim): a fading bar at the cursor after a big jump
-- and when a document gains focus:
--   vim.g.vsneo_beacon                        on/off (true)
--   vim.g.vsneo_beacon_min_jump               lines (10)
--   vim.g.vsneo_beacon_width                  columns (40)
--   vim.g.vsneo_beacon_duration               seconds (0.4)
-- Software rendering (Remote Desktop, a GPU-less VM, VS's hardware
-- acceleration off) turns particles, glow, smooth scrolling and the fading
-- blink styles off automatically:
--   vim.g.vsneo_reduce_effects                unset = detect, true = always
--                                             reduce, false = never
--   vim.g.vsneo_cursor_trail_size             0..1, how much it smears (0.8)
--   vim.g.vsneo_cursor_animate_in_insert_mode also animate typing (true)
-- and Neovide's particle effects (off by default, as in Neovide):
--   vim.g.vsneo_cursor_vfx_mode    effect names, a string or a list ('').
--     Neovide's: 'railgun' 'torpedo' 'pixiedust' 'sonicboom' 'ripple'
--     'wireframe' (on jumps). Cyberpunk: 'glitch' (jump, mode change,
--     focus), 'matrix' (jump), 'circuit' (jump), 'scanline' (jump, focus),
--     'sparks' (typing), 'flicker' (mode change, focus). Unknown names are
--     ignored. The effects live in Editor/Effects (ICursorEffect).
--   vim.g.vsneo_cursor_vfx_opacity                      0..255 (200)
--   vim.g.vsneo_cursor_vfx_particle_lifetime            seconds (0.5)
--   vim.g.vsneo_cursor_vfx_particle_highlight_lifetime  seconds (0.2)
--   vim.g.vsneo_cursor_vfx_particle_density             (0.7)
--   vim.g.vsneo_cursor_vfx_particle_speed               (10.0)
--   vim.g.vsneo_cursor_vfx_particle_phase               railgun (1.5)
--   vim.g.vsneo_cursor_vfx_particle_curl                railgun/torpedo (1.0)
-- Sent after the rc, and again on every SourcePost so ':source ~/.vsneorc'
-- applies live. Integers on the wire (ms, per mille).
------------------------------------------------------------------

local function truthy(v, default)
  if v == nil then return default end
  return v ~= false and v ~= 0
end

local function milli(v, default)
  local n = tonumber(v)
  if n == nil then n = default end
  return math.floor(n * 1000 + 0.5)
end

------------------------------------------------------------------
-- Presets (vim.g.vsneo_preset, :VSNeoPreset)
--
-- A preset is a whole look - colors, effects, cursor shape, trail, mode
-- line - under one name. It is never written into vim.g: every setting
-- above is read through opt(), so switching presets leaves nothing behind.
-- Who wins depends on how the preset was chosen:
--   vim.g.vsneo_preset (the rc) - a base: anything the rc sets still wins.
--   :VSNeoPreset <name>         - live, for this session: the preset's look
--                                 wins over the rc, so trying one needs no
--                                 rc edit. :VSNeoPreset none, or :source of
--                                 the rc, goes back to the rc's look.
------------------------------------------------------------------

local PRESETS = {
  -- Sodium-orange city, teal spinner lights, magenta neon, smoke.
  blade_runner = {
    cursor_style = { normal = 'block-outline', insert = 'line', replace = 'underline' },
    cursor_blinking = 'expand',
    cursor_color = { normal = '#FF6C11', insert = '#2DE2E6', replace = '#F706CF', operator = '#FFD319' },
    cursor_glow = true,
    cursor_animation_length = 0.16,
    cursor_trail_size = 0.85,
    cursor_vfx_mode = { 'scanline', 'torpedo', 'flicker', 'sparks' },
    cursor_vfx_opacity = 170,
    cursor_vfx_particle_lifetime = 0.6,
    cursor_vfx_particle_highlight_lifetime = 0.25,
    cursor_vfx_particle_density = 0.6,
    cursor_vfx_particle_speed = 7,
    cursor_vfx_particle_curl = 0.6,
    mode_line = true,
    mode_line_opacity = 0.10,
    beacon_duration = 0.5,
  },
  -- Phosphor green on black, digital rain; red pill replace, blue pill visual.
  matrix = {
    cursor_style = { normal = 'block-outline', insert = 'line', replace = 'underline' },
    cursor_blinking = 'phase',
    cursor_color = { normal = '#00FF41', insert = '#D1FFD6', replace = '#FF2A2A',
                     visual = '#2A7FFF', operator = '#B6FF00' },
    cursor_glow = true,
    cursor_animation_length = 0.12,
    cursor_trail_size = 0.9,
    cursor_vfx_mode = { 'matrix', 'sparks', 'flicker' },
    cursor_vfx_opacity = 220,
    cursor_vfx_particle_lifetime = 0.6,
    cursor_vfx_particle_highlight_lifetime = 0.2,
    cursor_vfx_particle_density = 1.0,
    cursor_vfx_particle_speed = 8,
    mode_line = true,
    mode_line_opacity = 0.08,
  },
  -- Night City yellow, netrunner cyan, Arasaka red.
  cyberpunk2077 = {
    cursor_style = { normal = 'block-outline', insert = 'line', replace = 'underline' },
    cursor_blinking = 'blink',
    cursor_color = { normal = '#FCEE0A', insert = '#00F0FF', replace = '#FF003C',
                     visual = '#FF2A6D', operator = '#FF003C' },
    cursor_glow = 10,
    cursor_animation_length = 0.10,
    cursor_trail_size = 0.7,
    cursor_vfx_mode = { 'glitch', 'circuit', 'scanline', 'sparks' },
    cursor_vfx_opacity = 230,
    cursor_vfx_particle_lifetime = 0.4,
    cursor_vfx_particle_highlight_lifetime = 0.18,
    cursor_vfx_particle_density = 0.8,
    cursor_vfx_particle_speed = 12,
    mode_line = true,
    mode_line_opacity = 0.10,
    beacon_duration = 0.3,
  },
  -- Editor themes rather than films: the theme's own accents per mode,
  -- a smooth blink and quiet effects, so the cursor sits in the palette
  -- instead of shouting over it. Light flavors skip the glow, which reads
  -- as a smudge on a light background.
  -- Tokyo Night (the VS Code theme): blue, green, red, purple, orange on #1A1B26.
  tokyo_night = {
    cursor_style = { normal = 'block', insert = 'line', replace = 'underline' },
    cursor_blinking = 'smooth',
    cursor_color = { normal = '#7AA2F7', insert = '#9ECE6A', replace = '#F7768E',
                     visual = '#BB9AF7', operator = '#FF9E64' },
    cursor_glow = 8,
    cursor_animation_length = 0.13,
    cursor_trail_size = 0.7,
    cursor_vfx_mode = { 'pixiedust' },
    cursor_vfx_opacity = 170,
    cursor_vfx_particle_lifetime = 0.5,
    cursor_vfx_particle_density = 0.5,
    cursor_vfx_particle_speed = 6,
    mode_line = true,
    mode_line_opacity = 0.08,
  },
  -- Tokyo Night Light: the same roles in its ink tones for a light background; no glow.
  tokyo_night_light = {
    cursor_style = { normal = 'block', insert = 'line', replace = 'underline' },
    cursor_blinking = 'smooth',
    cursor_color = { normal = '#2959AA', insert = '#33635C', replace = '#8C4351',
                     visual = '#7B43BA', operator = '#965027' },
    cursor_animation_length = 0.13,
    cursor_trail_size = 0.7,
    cursor_vfx_mode = { 'ripple' },
    cursor_vfx_opacity = 150,
    cursor_vfx_particle_lifetime = 0.5,
    cursor_vfx_particle_density = 0.5,
    cursor_vfx_particle_speed = 6,
    mode_line = true,
    mode_line_opacity = 0.10,
  },
  -- Catppuccin Mocha: lavender, green, red, mauve, peach.
  catppuccin_mocha = {
    cursor_style = { normal = 'block', insert = 'line', replace = 'underline' },
    cursor_blinking = 'smooth',
    cursor_color = { normal = '#B4BEFE', insert = '#A6E3A1', replace = '#F38BA8',
                     visual = '#CBA6F7', operator = '#FAB387' },
    cursor_glow = 8,
    cursor_animation_length = 0.13,
    cursor_trail_size = 0.7,
    cursor_vfx_mode = { 'ripple', 'pixiedust' },
    cursor_vfx_opacity = 170,
    cursor_vfx_particle_lifetime = 0.5,
    cursor_vfx_particle_density = 0.5,
    cursor_vfx_particle_speed = 6,
    mode_line = true,
    mode_line_opacity = 0.08,
  },
  -- Catppuccin Macchiato.
  catppuccin_macchiato = {
    cursor_style = { normal = 'block', insert = 'line', replace = 'underline' },
    cursor_blinking = 'smooth',
    cursor_color = { normal = '#B7BDF8', insert = '#A6DA95', replace = '#ED8796',
                     visual = '#C6A0F6', operator = '#F5A97F' },
    cursor_glow = 8,
    cursor_animation_length = 0.13,
    cursor_trail_size = 0.7,
    cursor_vfx_mode = { 'ripple', 'pixiedust' },
    cursor_vfx_opacity = 170,
    cursor_vfx_particle_lifetime = 0.5,
    cursor_vfx_particle_density = 0.5,
    cursor_vfx_particle_speed = 6,
    mode_line = true,
    mode_line_opacity = 0.08,
  },
  -- Catppuccin Frappe.
  catppuccin_frappe = {
    cursor_style = { normal = 'block', insert = 'line', replace = 'underline' },
    cursor_blinking = 'smooth',
    cursor_color = { normal = '#BABBF1', insert = '#A6D189', replace = '#E78284',
                     visual = '#CA9EE6', operator = '#EF9F76' },
    cursor_glow = 8,
    cursor_animation_length = 0.13,
    cursor_trail_size = 0.7,
    cursor_vfx_mode = { 'ripple', 'pixiedust' },
    cursor_vfx_opacity = 170,
    cursor_vfx_particle_lifetime = 0.5,
    cursor_vfx_particle_density = 0.5,
    cursor_vfx_particle_speed = 6,
    mode_line = true,
    mode_line_opacity = 0.08,
  },
  -- Catppuccin Latte, the light flavor; no glow.
  catppuccin_latte = {
    cursor_style = { normal = 'block', insert = 'line', replace = 'underline' },
    cursor_blinking = 'smooth',
    cursor_color = { normal = '#7287FD', insert = '#40A02B', replace = '#D20F39',
                     visual = '#8839EF', operator = '#FE640B' },
    cursor_animation_length = 0.13,
    cursor_trail_size = 0.7,
    cursor_vfx_mode = { 'ripple' },
    cursor_vfx_opacity = 150,
    cursor_vfx_particle_lifetime = 0.5,
    cursor_vfx_particle_density = 0.5,
    cursor_vfx_particle_speed = 6,
    mode_line = true,
    mode_line_opacity = 0.10,
  },
  -- Neovide out of the box: its trail timings, and nothing else. Every other
  -- look key is left out on purpose - live, that means no colors, no effects,
  -- no glow, Visual Studio's own caret.
  neovide = {
    cursor_animation_length = 0.15,
    cursor_trail_size = 1.0,
  },
}
local PRESET_NAMES = {
  'blade_runner', 'catppuccin_frappe', 'catppuccin_latte', 'catppuccin_macchiato',
  'catppuccin_mocha', 'cyberpunk2077', 'matrix', 'neovide', 'tokyo_night', 'tokyo_night_light',
}

-- 'Blade-Runner', 'blade runner' and 'blade_runner' are one name.
local function preset_key(name)
  if type(name) ~= 'string' then return nil end
  local key = vim.trim(name):lower():gsub('[%s%-]+', '_')
  return key
end

-- Every setting some preset defines: the "look". While a preset is live,
-- all of these come from it - a key it leaves out is the default, not the
-- rc's value, so the look is exactly the preset's whatever the rc holds.
local LOOK = {}
for _, preset in pairs(PRESETS) do
  for name in pairs(preset) do LOOK[name] = true end
end

-- The preset :VSNeoPreset chose, winning over the rc; nil when none is.
local live_preset = nil

local function active_preset()
  local key = live_preset or preset_key(vim.g.vsneo_preset)
  if key and PRESETS[key] then return key, PRESETS[key] end
  return nil, nil
end

--- A vsneo_<name> setting. A live preset owns the look; otherwise the
--- user's vim.g value, else the rc preset's.
local function opt(name)
  if live_preset and LOOK[name] then return PRESETS[live_preset][name] end
  local v = vim.g['vsneo_' .. name]
  if v ~= nil then return v end
  local _, preset = active_preset()
  return preset and preset[name]
end

-- Re-sourcing the rc is asking for the rc's look back. Registered before the
-- SourcePost pushes below, which then send it.
-- Paths compared normalized, not as an autocmd pattern: on Windows the
-- rc path is full of backslashes, which patterns treat as escapes.
local function rc_path(file) return vim.fs.normalize(vim.fn.fnamemodify(file, ':p')):lower() end
local RC_PATHS = { [rc_path('~/.vsneorc')] = true, [rc_path('~/.vsneorc.lua')] = true }
vim.api.nvim_create_autocmd('SourcePost', {
  group = group,
  callback = function(args)
    if live_preset and RC_PATHS[rc_path(args.file)] then live_preset = nil end
  end,
})

local function vfx_modes()
  local m = opt('cursor_vfx_mode')
  if type(m) == 'table' then return table.concat(m, ',') end
  if type(m) == 'string' then return m end
  return ''
end

-- nil -> -1 (detect), truthy -> 1 (always reduce), false/0 -> 0 (never).
local function reduce_effects()
  local v = vim.g.vsneo_reduce_effects
  if v == nil then return -1 end
  return truthy(v, false) and 1 or 0
end

-- Do not disturb (vim.g.vsneo_dnd, :VSNeoDnd): every animation and effect
-- off at once - trail, effects, smooth scrolling, beacon, custom cursor,
-- glow, mode line - so Visual Studio's plain caret is all there is. The
-- other settings are left alone and come back when it is turned off.
local function dnd() return truthy(vim.g.vsneo_dnd, false) end

local function send_cursor_animation()
  local quiet = dnd()
  local length = quiet and 0 or (tonumber(opt('cursor_animation_length')) or 0.13)
  local trail = tonumber(opt('cursor_trail_size')) or 0.8
  trail = math.max(0, math.min(1, trail))
  local opacity = tonumber(opt('cursor_vfx_opacity')) or 200
  vim.rpcnotify(chan, 'vsneo_cursor_animation',
    math.floor(math.max(0, length) * 1000 + 0.5),
    math.floor(trail * 1000 + 0.5),
    truthy(opt('cursor_animate_in_insert_mode'), true) and 1 or 0,
    quiet and '' or vfx_modes(),
    math.floor(math.max(0, math.min(255, opacity)) + 0.5),
    math.max(0, milli(opt('cursor_vfx_particle_lifetime'), 0.5)),
    math.max(0, milli(opt('cursor_vfx_particle_highlight_lifetime'), 0.2)),
    math.max(0, milli(opt('cursor_vfx_particle_density'), 0.7)),
    math.max(0, milli(opt('cursor_vfx_particle_speed'), 10.0)),
    milli(opt('cursor_vfx_particle_phase'), 1.5),
    milli(opt('cursor_vfx_particle_curl'), 1.0),
    quiet and 0 or math.max(0, milli(opt('cursor_short_animation_length'), 0.04)),
    quiet and 0 or math.max(0, milli(opt('scroll_animation_length'), 0.3)),
    math.max(0, math.floor(tonumber(opt('scroll_animation_far_lines')) or 1)),
    (not quiet and truthy(opt('beacon'), true)) and 1 or 0,
    math.max(1, math.floor(tonumber(opt('beacon_min_jump')) or 10)),
    math.max(1, math.floor(tonumber(opt('beacon_width')) or 40)),
    math.max(0, milli(opt('beacon_duration'), 0.4)),
    reduce_effects())
end

send_cursor_animation()
vim.api.nvim_create_autocmd('SourcePost', {
  group = group,
  callback = unless_runtime(send_cursor_animation),
})

------------------------------------------------------------------
-- VSNeo's own cursor (CustomCursorAdornment.cs)
--
-- Opt-in: while neither variable is set, Visual Studio draws its caret as
-- it always did. Set either and VSNeo hides the caret and draws its own:
--   opt('cursor_style')     'block' | 'block-outline' | 'line' |
--                                'line-thin' | 'underline' | 'underline-thin'
--     A string sets the normal-mode cursor; a table sets any of
--     normal, insert, replace, visual, operator, cmdline.
--     Defaults: block, line, underline, block, underline, (normal's).
--   opt('cursor_blinking')  'blink' | 'smooth' | 'phase' | 'expand' |
--                                'solid'   (VS Code's cursorBlinking; 'blink')
--   opt('cursor_color')     '#rrggbb' or a highlight group name (its bg,
--     else its fg), for every mode; or a table per mode like the style.
--     Unset modes use the theme's caret color. The trail and particles
--     follow it.
--   opt('cursor_glow')      true (12 px) or a blur radius in px: a neon
--     halo in the cursor's color. Off by default.
--   opt('mode_line')        tint the cursor line with the mode's color
--     (modes.nvim). Colors come from vsneo_cursor_color; uncolored modes use
--     modes.nvim's palette, normal stays untinted. Off by default.
--   opt('mode_line_opacity')  0..1 (0.12)
-- Visual mode is drawn by the visual-selection block either way.
------------------------------------------------------------------

local cursor_style_defaults = {
  normal = 'block', insert = 'line', replace = 'underline',
  visual = 'block', operator = 'underline',
}

-- '#rrggbb' -> 0xRRGGBB; a highlight group name -> its bg (else fg);
-- anything unusable -> -1, "the theme's caret color".
local function color_int(v)
  if type(v) == 'number' then return (v >= 0 and v <= 0xFFFFFF) and math.floor(v) or -1 end
  if type(v) ~= 'string' or v == '' then return -1 end
  if v:sub(1, 1) == '#' then
    local n = #v == 7 and tonumber(v:sub(2), 16) or nil
    return n or -1
  end
  local ok, hl = pcall(vim.api.nvim_get_hl, 0, { name = v, link = false })
  if ok and type(hl) == 'table' then return hl.bg or hl.fg or -1 end
  return -1
end

local cursor_modes = { 'normal', 'insert', 'replace', 'visual', 'operator', 'cmdline' }

local function send_cursor_style()
  local s = opt('cursor_style')
  local b = opt('cursor_blinking')
  local c = opt('cursor_color')
  local quiet = dnd()
  local enabled = not quiet and (truthy(s, false) or truthy(b, false) or truthy(c, false))

  -- A single color is every mode's; a table names modes, and cmdline
  -- follows normal unless named.
  local colors = {}
  for i, mode in ipairs(cursor_modes) do
    if type(c) == 'table' then
      colors[i] = color_int(c[mode])
    else
      colors[i] = color_int(c)
    end
  end
  if type(c) == 'table' and c.cmdline == nil then colors[6] = colors[1] end

  local glow = opt('cursor_glow')
  if glow == true then glow = 12 end
  glow = quiet and 0 or math.max(0, math.min(60, math.floor(tonumber(glow) or 0)))

  local styles = vim.deepcopy(cursor_style_defaults)
  if type(s) == 'string' then
    styles.normal = s
  elseif type(s) == 'table' then
    for k, v in pairs(s) do styles[k] = v end
  end
  if styles.cmdline == nil then styles.cmdline = styles.normal end

  vim.rpcnotify(chan, 'vsneo_cursor_style', enabled and 1 or 0,
    tostring(styles.normal), tostring(styles.insert), tostring(styles.replace),
    tostring(styles.visual), tostring(styles.operator), tostring(styles.cmdline),
    type(b) == 'string' and b or 'blink',
    colors[1], colors[2], colors[3], colors[4], colors[5], colors[6],
    glow,
    (not quiet and truthy(opt('mode_line'), false)) and 1 or 0,
    math.floor(math.max(0, math.min(1, tonumber(opt('mode_line_opacity')) or 0.12)) * 1000 + 0.5))
end

send_cursor_style()
-- ColorScheme too: a color given as a highlight group name follows the
-- colorscheme.
vim.api.nvim_create_autocmd({ 'SourcePost', 'ColorScheme' }, {
  group = group,
  callback = unless_runtime(send_cursor_style),
})

-- :VSNeoDnd [on|off] - toggles do-not-disturb (see dnd() above) live;
-- no argument flips it.
vim.api.nvim_create_user_command('VSNeoDnd', function(opts)
  local arg = vim.trim(opts.args):lower()
  local on
  if arg == 'on' then on = true
  elseif arg == 'off' then on = false
  elseif arg == '' then on = not dnd()
  else
    vim.api.nvim_echo({ { 'VSNeoDnd: expected on, off or nothing', 'ErrorMsg' } }, true, {})
    return
  end
  vim.g.vsneo_dnd = on
  send_cursor_animation()
  send_cursor_style()
  vim.api.nvim_echo({ { on and 'VSNeo: do not disturb - animations off'
                           or 'VSNeo: animations back on' } }, false, {})
end, {
  nargs = '?',
  complete = function() return { 'on', 'off' } end,
  desc = 'VSNeo: turn every cursor animation and effect off (or back on)',
})

-- :VSNeoPreset [name|none] - switches the preset live, for this session,
-- over whatever the rc sets (vim.g.vsneo_preset in the rc makes one stick,
-- as a base the rc can still override). none goes back to the rc's look. No
-- argument lists them.

vim.api.nvim_create_user_command('VSNeoPreset', function(opts)
  local arg = preset_key(opts.args) or ''
  if arg == '' then
    local current = active_preset() or 'none'
    local chunks = { { 'VSNeo presets: ' } }
    local names = vim.list_extend(vim.deepcopy(PRESET_NAMES), { 'none' })
    for i, name in ipairs(names) do
      if i > 1 then chunks[#chunks + 1] = { '  ' } end
      chunks[#chunks + 1] = name == current and { '[' .. name .. ']', 'Special' } or { name }
    end
    vim.api.nvim_echo(chunks, false, {})
    return
  end

  -- live_preset only, never vim.g: presets are read-through (opt), and
  -- writing the choice into vim.g.vsneo_preset destroyed the rc's own
  -- setting - "none" then left no preset at all instead of the rc's look.
  if arg == 'none' or arg == 'off' then
    live_preset = nil
  elseif PRESETS[arg] then
    live_preset = arg
  else
    vim.api.nvim_echo({ { 'VSNeoPreset: unknown preset "' .. vim.trim(opts.args) .. '" - one of '
                          .. table.concat(PRESET_NAMES, ', ') .. ', none', 'ErrorMsg' } }, true, {})
    return
  end
  send_cursor_animation()
  send_cursor_style()

  local key = active_preset()
  local msg = live_preset and ('VSNeo: preset ' .. live_preset .. ' (over your rc until :VSNeoPreset none)')
    or key and ('VSNeo: preset ' .. key .. " (your rc's)")
    or "VSNeo: no preset - your rc's look"
  vim.api.nvim_echo({ { msg } }, false, {})
end, {
  nargs = '?',
  complete = function(lead)
    local out = {}
    for _, name in ipairs(vim.list_extend(vim.deepcopy(PRESET_NAMES), { 'none' })) do
      if name:find(lead or '', 1, true) == 1 then out[#out + 1] = name end
    end
    return out
  end,
  desc = 'VSNeo: switch the cursor/effects preset (no argument lists them)',
})

------------------------------------------------------------------
-- Mapping table push (which-key data)
--
