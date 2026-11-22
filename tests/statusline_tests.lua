-- vsneo_statusline: [laststatus, fg, bg, {{text, fg, bg, bold}, ...}] for
-- StatusLineMargin. nvim's own laststatus stays 0 (its row would shorten the
-- window against Visual Studio's view); the value asked for decides whether
-- Visual Studio draws the line, and nvim_eval_statusline renders it.

local t = dofile('tests/helper.lua')

local h = t.setup({ name = 'C:/test/statusline.lua' })
vim.wait(20)
local r = t.report(h, 'vsneo_statusline')
t.expect(r ~= nil, 'pushed at load')
t.eq(r[2], 0, "nvim's default laststatus=2 is not a request")
t.eq(#r[5], 0, 'no segments while off')
t.eq(vim.o.laststatus, 0, 'forced off at load')

local function text(report)
  local parts = {}
  for _, seg in ipairs(report[5]) do parts[#parts + 1] = seg[1] end
  return table.concat(parts)
end

-- Live :set
vim.cmd('set laststatus=2')
vim.wait(20)
t.eq(vim.o.laststatus, 0, "a live :set laststatus must be forced back to 0")
r = t.report(h, 'vsneo_statusline')
t.eq(r[2], 2, 'laststatus=2 reaches the extension')
local line = text(r)
t.expect(line:find('NORMAL', 1, true) ~= nil, 'default line shows the mode: ' .. line)
t.expect(line:find('statusline.lua', 1, true) ~= nil, 'default line shows the file: ' .. line)
t.expect(r[5][1][3] ~= -1, 'the mode chip carries its color')

-- The margin's width, not nvim's window, is what %= fills to
vsneo.statusline_width(137)
vim.wait(20)
t.eq(vim.api.nvim_strwidth(text(t.report(h, 'vsneo_statusline'))), 137, 'the line spans the sent width')

-- Only changes are sent
t.clear(h)
vim.cmd('doautocmd CursorMoved')
vim.wait(20)
t.eq(t.report(h, 'vsneo_statusline'), nil, 'an unchanged line is not resent')

-- The user's own format, with highlight groups
vim.api.nvim_set_hl(0, 'StlTest', { fg = 0x112233, bg = 0x445566, bold = true })
vim.cmd('set statusline=%#StlTest#hello%*\\ world')
vim.wait(20)
r = t.report(h, 'vsneo_statusline')
t.eq(text(r), 'hello world', 'a custom statusline is evaluated')
t.eq(r[5][1][2], 0x112233, 'segment fg from its group')
t.eq(r[5][1][3], 0x445566, 'segment bg from its group')
t.eq(r[5][1][4], true, 'segment bold from its group')

-- A broken function shows an error instead of failing every key
vim.cmd('set statusline=%{%v:lua.nope()%}')
vim.wait(20)
r = t.report(h, 'vsneo_statusline')
t.expect(text(r):find('statusline:', 1, true) == 1, 'an error is shown in the line')
vim.v.errmsg = ''
vim.cmd('normal! j')
vim.wait(20)
t.eq(vim.v.errmsg, '', 'a broken statusline errors once, not on every key')

-- The rc path: set with noautocmd, then sourced
vim.cmd('set statusline=')
vim.cmd('set laststatus=0')
vim.wait(20)
t.eq(t.report(h, 'vsneo_statusline')[2], 0, 'off before the rc path')
vim.cmd('noautocmd set laststatus=2')
vim.cmd('doautocmd SourcePost')
vim.wait(20)
t.eq(vim.o.laststatus, 0, 'an rc laststatus must be reasserted to 0')
t.eq(t.report(h, 'vsneo_statusline')[2], 2, 'the rc laststatus reaches the extension')

vim.cmd('set laststatus=0')
vim.wait(20)
r = t.report(h, 'vsneo_statusline')
t.eq(r[2], 0, 'laststatus=0 turns it off again')
t.eq(#r[5], 0, 'no segments once off')

print('statusline_tests: ALL OK')
