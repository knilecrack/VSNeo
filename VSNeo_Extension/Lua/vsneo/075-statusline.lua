------------------------------------------------------------------
-- Statusline (laststatus=2)
--
-- nvim's own status line stays off: its row would be a row nvim's window
-- lacks next to the Visual Studio view (070-visual.lua). The 'statusline'
-- the user asked for is still evaluated here, by nvim_eval_statusline, and
-- Visual Studio draws the result under each view (StatusLineMargin). So a
-- hand-written %-format, lualine and heirline all work - nvim renders them.
-- Click handlers (%@) do not.
--
-- vsneo_statusline is [laststatus, fg, bg, segments]: fg/bg are the
-- StatusLine group's (-1 unset), segments are {text, fg, bg, bold}. Sent
-- only when something changed; with laststatus ~= 2 the segments are empty.
------------------------------------------------------------------

local mode_names = {
  n = { 'NORMAL', 'Normal' }, no = { 'O-PENDING', 'Pending' },
  i = { 'INSERT', 'Insert' }, R = { 'REPLACE', 'Replace' },
  v = { 'VISUAL', 'Visual' }, V = { 'V-LINE', 'Visual' }, ['\22'] = { 'V-BLOCK', 'Visual' },
  s = { 'SELECT', 'Visual' }, S = { 'S-LINE', 'Visual' }, ['\19'] = { 'S-BLOCK', 'Visual' },
  c = { 'COMMAND', 'Command' }, t = { 'TERMINAL', 'Terminal' },
}

-- The mode as a colored chip, for the default line and for users' own:
-- set statusline=%{%v:lua.vsneo.statusline_mode()%}\ %f
function vsneo.statusline_mode()
  local m = vim.api.nvim_get_mode().mode
  local entry = mode_names[m:sub(1, 2)] or mode_names[m:sub(1, 1)] or { m, 'Normal' }
  return '%#VSNeoStatusMode' .. entry[2] .. '# ' .. entry[1] .. ' %*'
end

-- The mode badge's palette (ModeStatusBarItem), as ordinary highlight
-- groups: default = true, so a colorscheme or the rc can restyle them.
local function define_mode_colors()
  local dark, light = 0x1E1E1E, 0xF5F5F5
  for group, c in pairs({
    Normal = { 0x5B8C3E, dark }, Pending = { 0x556070, light },
    Insert = { 0x2F6FBF, light }, Replace = { 0xB0392E, light },
    Visual = { 0x8A4FA3, light }, Command = { 0xB07D1E, dark },
    Terminal = { 0x2E8B8B, light },
  }) do
    vim.api.nvim_set_hl(0, 'VSNeoStatusMode' .. group,
      { bg = c[1], fg = c[2], bold = true, default = true })
  end
end
define_mode_colors()

local default_statusline = '%{%v:lua.vsneo.statusline_mode()%} %t%( %m%r%)%=%l:%c  %P '
-- nvim 0.11+ ships a non-empty default; that is no more a choice than ''.
local nvim_default_statusline = vim.api.nvim_get_option_info2('statusline', {}).default

local function colors(name)
  local ok, hl = pcall(vim.api.nvim_get_hl, 0, { name = name, link = false })
  if not ok or not hl then return -1, -1, false end
  local fg, bg = hl.fg or -1, hl.bg or -1
  if hl.reverse then fg, bg = bg, fg end
  return fg, bg, hl.bold == true
end

local sent_statusline, broken_statusline, broken_error
local function push_statusline()
  local base_fg, base_bg = colors('StatusLine')
  local segments, key = {}, { statusline.laststatus, base_fg, base_bg }
  if statusline.laststatus == 2 then
    local fmt = vim.o.statusline
    if fmt == '' or fmt == nvim_default_statusline then fmt = default_statusline end
    -- A failing %{} reports its error as a message and still returns a
    -- line, so the error shows only in v:errmsg. Vim empties a statusline
    -- that errors; here the format is set aside, so one broken function
    -- costs one error message, not one per key.
    local ok, r = true, nil
    if fmt ~= broken_statusline then
      vim.v.errmsg = ''
      ok, r = pcall(vim.api.nvim_eval_statusline, fmt,
        { winid = 0, highlights = true, maxwidth = statusline.width })
      if ok and vim.v.errmsg ~= '' then ok, r = false, vim.v.errmsg end
      if not ok then broken_statusline, broken_error = fmt, tostring(r) end
    end
    if fmt == broken_statusline then
      r = { str = 'statusline: ' .. broken_error, highlights = {} }
    end
    local hls = r.highlights
    if #hls == 0 or hls[1].start > 0 then
      table.insert(hls, 1, { start = 0, group = 'StatusLine' })
    end
    for i, h in ipairs(hls) do
      local stop = hls[i + 1] and hls[i + 1].start or #r.str
      local text = r.str:sub(h.start + 1, stop)
      if text ~= '' then
        -- 0.11+ reports stacked groups; the last one is the one on top.
        local group = h.group or (h.groups and h.groups[#h.groups]) or 'StatusLine'
        local fg, bg, bold = colors(group)
        if fg == -1 then fg = base_fg end
        if bg == -1 then bg = base_bg end
        segments[#segments + 1] = { text, fg, bg, bold }
        key[#key + 1] = text .. '\0' .. fg .. '\0' .. bg .. '\0' .. tostring(bold)
      end
    end
  end
  key = table.concat(key, '\1')
  if key == sent_statusline then return end
  sent_statusline = key
  vim.rpcnotify(chan, 'vsneo_statusline', statusline.laststatus, base_fg, base_bg, segments)
end

-- The margin's width in columns. It spans the whole view - line numbers,
-- glyph margin, scrollbar - while nvim's window is only the text area, so
-- with the window's width %= left the right part a scrollbar-and-gutter
-- short of the edge. Sent on resize, zoom and focus, never per key.
function vsneo.statusline_width(columns)
  columns = tonumber(columns)
  statusline.width = columns and columns > 0 and math.floor(columns) or nil
  statusline.push()
end

-- Every trigger of a key lands here; one evaluation per batch.
local statusline_scheduled = false
function statusline.push()
  if statusline_scheduled then return end
  statusline_scheduled = true
  vim.schedule(function()
    statusline_scheduled = false
    push_statusline()
  end)
end

vim.api.nvim_create_autocmd({
  'CursorMoved', 'CursorMovedI', 'ModeChanged', 'BufEnter', 'BufModifiedSet',
  'BufWritePost', 'RecordingEnter', 'RecordingLeave', 'WinResized', 'DiagnosticChanged',
}, {
  group = group,
  callback = statusline.push,
})

-- A live ':set statusline=...' moves nothing else.
vim.api.nvim_create_autocmd('OptionSet', {
  group = group,
  pattern = 'statusline',
  callback = statusline.push,
})

vim.api.nvim_create_autocmd('ColorScheme', {
  group = group,
  callback = function()
    define_mode_colors()
    statusline.push()
  end,
})

statusline.push()
