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

-- The user's v:errmsg survives the refreshes (mappings and plugins read it)
vim.v.errmsg = 'E492: Not an editor command: foo'
vim.cmd('doautocmd CursorMoved')
vim.wait(20)
t.eq(vim.v.errmsg, 'E492: Not an editor command: foo', 'a refresh must not clear v:errmsg')

-- A broken function is reported once, not on every key, and v:errmsg stays
local notes = {}
local notify = vim.notify
vim.notify = function(msg) notes[#notes + 1] = msg end
vim.v.errmsg = ''
vim.cmd('set statusline=%{%v:lua.nope()%}')
vim.wait(20)
vim.cmd('doautocmd CursorMoved')
vim.wait(20)
vim.cmd('normal! j')
vim.cmd('doautocmd CursorMoved')
vim.wait(20)
t.eq(#notes, 1, 'a broken statusline is reported once: ' .. vim.inspect(notes))
t.expect(notes[1]:find('nope', 1, true) ~= nil, 'the report names the error: ' .. tostring(notes[1]))
t.eq(vim.v.errmsg, '', 'the statusline error does not land in v:errmsg')

-- A component that fails on purpose under silent! keeps the line working
function _G.stl_silent()
  vim.cmd('silent! normal! zc')   -- E490: no fold here
  return 'still here'
end
vim.cmd('set statusline=%{v:lua.stl_silent()}')
vim.wait(20)
vim.cmd('normal! k')
vim.cmd('doautocmd CursorMoved')
vim.wait(20)
t.eq(text(t.report(h, 'vsneo_statusline')), 'still here', 'silent! inside a component does not break the line')
vim.notify = notify

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

-- 'modified' (%m, [+]) follows Visual Studio's dirty flag: a prime of a
-- saved file is a buffer write, and must not read [+]; a save in Visual
-- Studio (Ctrl+S never reaches nvim's :w) clears it.
vim.cmd('set laststatus=2 statusline=%t%m')
vsneo.set_all_lines(0, { 'primed' }, false)
t.eq(vim.bo.modified, false, 'a prime of a saved file is not modified')
vsneo.set_all_lines(0, { 'primed', 'dirty' }, true)
t.eq(vim.bo.modified, true, 'a prime of a dirty document is modified')
vsneo.set_modified(vim.api.nvim_get_current_buf(), false)
vim.wait(20)
t.eq(vim.bo.modified, false, 'a Visual Studio save clears modified')
t.eq(text(t.report(h, 'vsneo_statusline')), 'statusline.lua', 'and %m with it')
vsneo.set_modified(vim.api.nvim_get_current_buf(), true)
vim.wait(20)
t.eq(text(t.report(h, 'vsneo_statusline')), 'statusline.lua[+]', 'a dirty document shows [+]')

-- An edit that makes Visual Studio clean again (undo back to the save
-- point, a reload) arrives after the dirty event; its spans carry the flag.
vsneo.apply_spans(0, { { 0, 0, 0, 0, { 'x' } } }, false)
t.eq(vim.bo.modified, false, 'spans that leave Visual Studio clean clear modified')
vsneo.apply_spans(0, { { 0, 0, 0, 1, { '' } } }, true)
t.eq(vim.bo.modified, true, 'spans that dirty Visual Studio set modified')

print('statusline_tests: ALL OK')
