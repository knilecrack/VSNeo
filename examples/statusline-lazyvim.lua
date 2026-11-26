-- LazyVim-style statusline for VSNeo - paste into ~/.vsneorc.lua
--
-- LazyVim's bar is lualine with the tokyonight theme. lualine itself does
-- not fit here: LazyVim runs it as one global bar (laststatus=3), and its
-- git-diff and LSP-diagnostics sections read state that lives in Visual
-- Studio, not nvim. This is the same look in plain Lua, no plugins:
--
--    NORMAL   main   Editor/Keys/VsNeoKeyProcessor.cs ●        cs   42%  120:8    14:05
--   [  a   ][   b    ][               c                ]       [ x ][     y      ][  z   ]
--
-- The arrows and icons are Nerd Font glyphs, drawn in the editor's font:
-- set nerd = false below if they show as boxes, or pick a Nerd Font in
-- Tools > Options > Environment > Fonts and Colors (Text Editor).
-- The clock refreshes with the line, on keys and mode changes.

local nerd = true

vim.o.laststatus = 2   -- one line under each view; VSNeo draws it

local glyph = nerd and {
  left = '\u{e0b0}', right = '\u{e0b2}', branch = '\u{e0a0} ', clock = '\u{f017} ', modified = ' \u{25cf}',
} or {
  left = '', right = '', branch = '', clock = '', modified = ' [+]',
}

-- catppuccin frappe, laid out as catppuccin's lualine theme does: the bar
-- is mantle, section b surface0, the mode chip's text crust. For LazyVim's
-- own tokyonight (night) instead:
--   bar = 0x16161e, fg = 0xa9b1d6, dim = 0x565f89, section_b = 0x3b4261, dark = 0x15161e,
--   blue = 0x7aa2f7, green = 0x9ece6a, magenta = 0xbb9af7, red = 0xf7768e,
--   yellow = 0xe0af68, teal = 0x1abc9c, orange = 0xff9e64,
local c = {
  bar = 0x292c3c, fg = 0xc6d0f5, dim = 0x838ba7, section_b = 0x414559, dark = 0x232634,
  blue = 0x8caaee, green = 0xa6d189, magenta = 0xca9ee6, red = 0xe78284,
  yellow = 0xef9f76, teal = 0x81c8be, orange = 0xef9f76,
}

local modes = {
  n = { 'NORMAL', 'Normal' }, no = { 'O-PENDING', 'Normal' },
  i = { 'INSERT', 'Insert' }, R = { 'REPLACE', 'Replace' },
  v = { 'VISUAL', 'Visual' }, V = { 'V-LINE', 'Visual' }, ['\22'] = { 'V-BLOCK', 'Visual' },
  s = { 'SELECT', 'Visual' }, S = { 'S-LINE', 'Visual' }, ['\19'] = { 'S-BLOCK', 'Visual' },
  c = { 'COMMAND', 'Command' }, t = { 'TERMINAL', 'Terminal' },
}
local mode_colors = {
  Normal = c.blue, Insert = c.green, Visual = c.magenta,
  Replace = c.red, Command = c.yellow, Terminal = c.teal,
}

local function highlights()
  local hl = vim.api.nvim_set_hl
  hl(0, 'StatusLine', { fg = c.fg, bg = c.bar })   -- section c, and the gap
  hl(0, 'LzSepBC', { fg = c.section_b, bg = c.bar })
  hl(0, 'LzFile', { fg = c.fg, bg = c.bar, bold = true })
  hl(0, 'LzDir', { fg = c.dim, bg = c.bar })
  hl(0, 'LzMod', { fg = c.orange, bg = c.bar, bold = true })
  for name, color in pairs(mode_colors) do
    hl(0, 'LzA' .. name, { fg = c.dark, bg = color, bold = true })
    hl(0, 'LzB' .. name, { fg = color, bg = c.section_b })
    hl(0, 'LzSepAB' .. name, { fg = color, bg = c.section_b })
    hl(0, 'LzSepAC' .. name, { fg = color, bg = c.bar })
  end
end
highlights()
vim.api.nvim_create_autocmd('ColorScheme', { callback = highlights })

-- The git branch, looked up once per directory, off the main loop. It
-- appears with the next refresh of the line (the next key).
local branches = {}
local function branch(dir)
  if dir == '' then return '' end
  if branches[dir] == nil then
    branches[dir] = ''
    pcall(vim.system, { 'git', '-C', dir, 'branch', '--show-current' }, { text = true }, function(r)
      if r.code == 0 then branches[dir] = vim.trim(r.stdout or '') end
    end)
  end
  return branches[dir]
end

local function esc(s) return (s:gsub('%%', '%%%%')) end

-- The last three parts of the path, the name in bold: LazyVim's pretty_path.
local function path()
  local name = vim.api.nvim_buf_get_name(0)
  if name == '' then return '%#LzDir#[No Name]' end
  local parts = vim.split(name:gsub('\\', '/'), '/', { trimempty = true })
  local file = table.remove(parts)
  local dirs = {}
  for i = math.max(1, #parts - 1), #parts do dirs[#dirs + 1] = parts[i] end
  local dir = #dirs > 0 and (table.concat(dirs, '/') .. '/') or ''
  local mod = vim.bo.modified and ('%#LzMod#' .. glyph.modified) or ''
  return '%#LzDir#' .. esc(dir) .. '%#LzFile#' .. esc(file) .. mod
end

function _G.lazyvim_statusline()
  local m = vim.api.nvim_get_mode().mode
  local mode = modes[m:sub(1, 2)] or modes[m:sub(1, 1)] or { m, 'Normal' }
  local name, kind = mode[1], mode[2]
  local A, B, AB = '%#LzA' .. kind .. '#', '%#LzB' .. kind .. '#', '%#LzSepAB' .. kind .. '#'

  local br = branch(vim.fn.expand('%:p:h'))
  -- No branch: no section b, the arrow runs from the mode straight to the bar.
  local ab = br ~= ''
    and (AB .. glyph.left .. B .. ' ' .. glyph.branch .. esc(br) .. ' %#LzSepBC#' .. glyph.left)
    or ('%#LzSepAC' .. kind .. '#' .. glyph.left)
  local ft = vim.bo.filetype ~= '' and ('%#LzDir#' .. vim.bo.filetype .. ' ') or ''

  return table.concat({
    -- %<: in a narrow view the path is cut, not the mode.
    A, ' ', name, ' ', ab, '%#StatusLine# %<', path(),
    '%=', ft,
    '%#LzSepBC#', glyph.right, B, ' %P ', ' %l:%c ',
    AB, glyph.right, A, ' ', glyph.clock, os.date('%H:%M'), ' ',
  })
end

vim.o.statusline = '%{%v:lua.lazyvim_statusline()%}'
