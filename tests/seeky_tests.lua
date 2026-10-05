-- vsneo.seeky(mode, query): asks the extension to show the embedded Seeky
-- picker by sending the vsneo_seeky notification (mode, query-or-'').
-- :Seeky [mode] [query] is the command form over the same call.

-- No ~/.vsneorc*: the <leader>s defaults below are only made where the rc
-- leaves the keys free, and a developer's own rc must not decide the result.
vim.env.HOME = vim.fn.tempname()
vim.env.USERPROFILE = vim.env.HOME
local t = dofile('tests/helper.lua')
local handle = t.setup({ name = 'C:/test/seeky.lua' })

t.eq(vsneo.seeky('files'), true, 'files returns true')
t.eq(vsneo.seeky('grep', 'foo bar|baz'), true, 'grep with query returns true')
t.eq(vsneo.seeky('symbols'), true, 'symbols returns true')

local all = t.reports(handle, 'vsneo_seeky')
t.eq(#all, 3, 'one notification per call')
t.eq(all[1][2], 'files', 'mode is the first argument')
t.eq(all[1][3], '', 'missing query sends the empty string')
t.eq(all[2][2], 'grep', 'grep mode')
t.eq(all[2][3], 'foo bar|baz', 'query is verbatim, pipes included')
t.eq(all[3][2], 'symbols', 'symbols mode')
t.eq(all[3][3], '', 'missing query sends the empty string')

-- The pick lands far away: the call records the jump, so '' comes back.
vim.api.nvim_win_set_cursor(0, { 40, 0 })
vsneo.seeky('files')
vim.api.nvim_win_set_cursor(0, { 70, 0 })
vim.cmd("normal! ''")
t.eq(vim.api.nvim_win_get_cursor(0)[1], 40, "'' returns to where the picker was opened")

-- :Seeky with no argument opens files; the rest of the line is the query.
local function last()
  local r = t.report(handle, 'vsneo_seeky')
  return r[2], r[3]
end

vim.cmd('Seeky')
local mode, query = last()
t.eq(mode, 'files', ':Seeky alone opens files')
t.eq(query, '', ':Seeky alone has no query')

vim.cmd('Seeky grep  foo  bar')
mode, query = last()
t.eq(mode, 'grep', ':Seeky grep')
t.eq(query, 'foo  bar', 'query is the rest of the line, inner spacing kept')

vim.cmd('Seeky outline')
mode = last()
t.eq(mode, 'outline', 'outline is passed through (the extension maps it to the page mode)')

-- From visual mode with no query, the selection's first line is the query.
vim.api.nvim_buf_set_lines(0, 0, 2, false, { '  needle in hay', 'second line' })
vim.api.nvim_win_set_cursor(0, { 1, 2 })
vim.cmd('normal! vee\27')
vim.cmd("'<,'>Seeky grep")
mode, query = last()
t.eq(mode, 'grep', 'range form keeps the mode')
t.eq(query, 'needle in', 'range form greps the selection')

vim.cmd('normal! Vj\27')
vim.cmd("'<,'>Seeky grep")
_, query = last()
t.eq(query, 'needle in hay', 'a multi-line selection takes its first line, trimmed')

vim.cmd("'<,'>Seeky grep explicit")
_, query = last()
t.eq(query, 'explicit', 'an explicit query wins over the range')

vim.cmd('Seeky lines')
mode = last()
t.eq(mode, 'lines', 'lines (current file) is passed through')

vim.cmd('Seeky resume')
mode, query = last()
t.eq(mode, 'resume', 'resume is passed through')
t.eq(query, '', 'resume carries no query')

-- Completion offers modes for the first argument only.
local modes = vim.fn.getcompletion('Seeky ou', 'cmdline')
t.eq(#modes, 1, 'one mode starts with ou')
t.eq(modes[1], 'outline', 'outline completes')
t.eq(#vim.fn.getcompletion('Seeky grep fo', 'cmdline'), 0, 'no completion inside the query')
local r = vim.fn.getcompletion('Seeky res', 'cmdline')
t.eq(#r, 1, 'one mode starts with res')
t.eq(r[1], 'resume', 'resume completes')

-- <leader>s defaults: each sends its mode, carries a desc for which-key.
local function leader_map(keys) return vim.fn.maparg('<leader>' .. keys, 'n', false, true) end
for keys, want in pairs({ sf = 'files', sg = 'grep', ['s/'] = 'lines', ss = 'symbols',
                          so = 'outline', sm = 'git', sr = 'resume' }) do
  local map = leader_map(keys)
  t.eq(map.desc ~= nil and map.desc:sub(1, 7), 'Seeky: ', '<leader>' .. keys .. ' has a Seeky desc')
  map.callback()
  mode, query = last()
  t.eq(mode, want, '<leader>' .. keys .. ' opens ' .. want)
  t.eq(query, '', '<leader>' .. keys .. ' carries no query')
end
vim.api.nvim_buf_set_lines(0, 0, 1, false, { 'local needle_word = 1' })
vim.api.nvim_win_set_cursor(0, { 1, 8 })
leader_map('sw').callback()
mode, query = last()
t.eq(mode, 'grep', '<leader>sw greps')
t.eq(query, 'needle_word', '<leader>sw takes the word under the cursor')

-- vsneo_seeky_config: the colorscheme's palette and the prompt-normal switch,
-- re-sent on ColorScheme.
vim.cmd('hi NormalFloat guibg=#112233 guifg=#aabbcc')
vim.cmd('hi TelescopeMatching guifg=#ff8800')
vim.g.vsneo_seeky_prompt_normal = true
vim.cmd('doautocmd ColorScheme')
local cfg = t.report(handle, 'vsneo_seeky_config')
t.eq(cfg[2].bg, '#112233', 'palette bg from NormalFloat')
t.eq(cfg[2].fg, '#aabbcc', 'palette fg from NormalFloat')
t.eq(cfg[2]['mhl-fg'], '#ff8800', 'matches colored by TelescopeMatching')
t.eq(cfg[3], 1, 'prompt normal mode on')
for _, name in ipairs({ 'bg-alt', 'bg-sel', 'border', 'fg-dim', 'accent', 'hot', 'amber',
                        'green', 'hl', 'mhl-bg' }) do
  t.eq(type(cfg[2][name]), 'string', 'palette fills ' .. name)
end

-- vsneo.seeky_source: the rows for the pickers only nvim can fill.
vim.api.nvim_buf_set_lines(0, 0, -1, false, { 'alpha', '  beta line', 'gamma' })
vim.api.nvim_win_set_cursor(0, { 2, 2 })
vim.cmd('normal! ma')
local rows = vsneo.seeky_source('marks')
local mark_a
for _, row in ipairs(rows) do if row.name == 'a' then mark_a = row end end
t.expect(mark_a ~= nil, 'mark a is listed')
t.eq(mark_a.line, 2, 'mark line')
t.eq(mark_a.text, 'beta line', 'mark row carries its line, trimmed')
t.eq(mark_a.path, vim.api.nvim_buf_get_name(0), 'mark path is the buffer')

vim.fn.setreg('q', 'first\nsecond')
local reg_q
for _, row in ipairs(vsneo.seeky_source('registers')) do if row.name == 'q' then reg_q = row end end
t.expect(reg_q ~= nil, 'register q is listed')
t.eq(reg_q.preview, 'first\nsecond', 'register preview is the full value')

vim.keymap.set('n', '<leader>zz', '<Nop>', { desc = 'test mapping' })
vim.keymap.set('n', '<Plug>(hidden)', '<Nop>')
local found_map, plug = nil, false
for _, row in ipairs(vsneo.seeky_source('keymaps')) do
  if row.text == 'test mapping' then found_map = row end
  if row.name:find('<Plug>', 1, true) then plug = true end
end
t.expect(found_map ~= nil, 'a described mapping is listed')
t.eq(found_map.name, vim.fn.keytrans(vim.api.nvim_replace_termcodes('<leader>zz', true, true, true)),
  'keymap name is typeable key notation')
t.eq(plug, false, '<Plug> mappings are left out')

vim.fn.histadd(':', 'echo 1')
vim.fn.histadd(':', 'echo 2')
vim.fn.histadd(':', 'echo 1')
local hist = vsneo.seeky_source('command_history')
t.eq(hist[1].name, 'echo 1', 'most recent command first')
t.eq(hist[2].name, 'echo 2', 'then the one before')
local dupes = 0
for _, row in ipairs(hist) do if row.name == 'echo 1' then dupes = dupes + 1 end end
t.eq(dupes, 1, 'history is deduplicated')
vim.fn.histadd('/', 'needle')
t.eq(vsneo.seeky_source('search_history')[1].name, 'needle', 'search history')
t.eq(#vsneo.seeky_source('nonsense'), 0, 'unknown mode lists nothing')

-- A user mapping that merely overlaps the keys keeps the default away: the
-- surround mappings in examples/vsneorc.vim (<leader>sw) and friends) would
-- otherwise wait out 'timeoutlen'. Re-sourced with one in place.
local h2 = t.setup({ name = 'C:/test/seeky2.lua' })
vim.keymap.del('n', '<leader>sw')
vim.keymap.set('n', '<leader>sw)', 'ciW()<Esc>')
local chunk = assert(loadfile('VSNeo_Extension/Lua/vsneo.lua'))
chunk(1)
t.eq(vim.fn.maparg('<leader>sw', 'n'), '', 'an overlapping user mapping keeps <leader>sw unmapped')
t.eq(vim.fn.maparg('<leader>sf', 'n', false, true).desc, 'Seeky: files', 'the others still map')

print('seeky_tests: ALL OK')
