-- The user's config is one thing or the other: init.lua / init.vim in the
-- config directory, else the ~/.vsneorc pair. Each case needs its own nvim
-- with its own HOME and config directory, started as the extension starts
-- it (-u NORC, g:vsneo set by --cmd), so this drives children over RPC.

local companion = vim.fn.fnamemodify('VSNeo_Extension/Lua/vsneo.lua', ':p')
local root = vim.fs.normalize(vim.fn.tempname())
local children = {}

local function write(path, lines)
  vim.fn.mkdir(vim.fs.dirname(path), 'p')
  vim.fn.writefile(lines, path)
end

--- Starts a child on a fresh home; files maps a path under it to its lines.
local function start(name, files)
  local home = root .. '/' .. name
  vim.fn.mkdir(home, 'p')
  for path, lines in pairs(files) do write(home .. '/' .. path, lines) end
  local chan = vim.fn.jobstart(
    { vim.v.progpath, '--embed', '--headless', '-u', 'NORC', '-i', 'NONE', '--cmd', 'let g:vsneo=1' },
    { rpc = true, env = { HOME = home, USERPROFILE = home, XDG_CONFIG_HOME = home .. '/cfg',
      XDG_DATA_HOME = home .. '/data', NVIM_APPNAME = 'vsneo' } })
  assert(chan > 0, 'could not start child nvim')
  children[#children + 1] = chan
  local function lua(code, ...) return vim.rpcrequest(chan, 'nvim_exec_lua', code, { ... }) end
  lua([[
    _G.sent = {}
    vim.rpcnotify = function(_, method, ...) _G.sent[#_G.sent + 1] = { method, ... } end
    assert(loadfile(...))(0)
  ]], companion)
  return lua
end

local function finish()
  for _, chan in ipairs(children) do vim.fn.jobstop(chan) end
  vim.fn.delete(root, 'rf')
end

local function expect(cond, msg)
  if not cond then finish() error('FAILED: ' .. msg, 2) end
end

local LAST_IMAPS = [[
  local last
  for _, n in ipairs(_G.sent) do if n[1] == 'vsneo_imaps' then last = n[2] end end
  return last or {}
]]
local function claimed(lua, lhs) return vim.tbl_contains(lua(LAST_IMAPS), lhs) end

------------------------------------------------------------------
-- init.lua present: it is the config, the rc pair is not read
------------------------------------------------------------------
local lua = start('init', {
  ['cfg/vsneo/lua/mine.lua'] = { 'return { value = 42 }' },
  ['cfg/vsneo/init.lua'] = {
    'vim.g.init_saw_vsneo = vim.g.vsneo',
    "vim.g.init_saw_api = type(vsneo) == 'table' and type(vsneo.cmd) == 'function'",
    "vim.g.init_required = require('mine').value",
    'vim.o.wrap = true',
    "vim.o.inccommand = 'split'",
    "vim.keymap.set('n', 'gd', '<Nop>', { desc = 'mine' })",
    "vim.keymap.set('i', '<CR>', '<CR>x')",
    "vim.keymap.set('i', '<Left>', '<Esc>')",
    -- what lazy.nvim's setup does to the runtimepath
    "vim.opt.runtimepath = { vim.fn.stdpath('config'), vim.env.VIMRUNTIME }",
  },
  ['.vsneorc.lua'] = { 'vim.g.legacy_ran = 1' },
})

expect(lua('return vim.g.init_saw_vsneo') == 1, 'init.lua did not see vim.g.vsneo')
expect(lua('return vim.g.init_saw_api') == true, 'vsneo.cmd did not exist when init.lua ran')
expect(lua('return vim.g.init_required') == 42, "the config directory's lua/ is not requirable")
expect(lua('return vim.g.legacy_ran') == vim.NIL, '~/.vsneorc.lua was read beside init.lua')
expect(lua("return vim.fn.maparg('gd', 'n', false, true).desc") == 'mine',
  "init.lua's gd lost to the companion default")
expect(lua('return vim.o.wrap') == false, "init.lua's wrap survived the forced options")
expect(lua('return vim.o.inccommand') == '', "init.lua's inccommand survived the forced options")
expect(lua('return vim.env.MYVIMRC'):find('init.lua', 1, true) ~= nil, '$MYVIMRC is not the init file')
expect(lua("return vim.fn.execute('messages')"):find('are not read while', 1, true) ~= nil,
  'no notice that the rc pair is ignored')

-- a config that resets the runtimepath (lazy.nvim) must not take away what
-- nvim's startup had already loaded: matchit's % calls an autoload function
lua([[
  vim.api.nvim_buf_set_lines(0, 0, -1, false, { 'if (a) { b }' })
  vim.api.nvim_win_set_cursor(0, { 1, 3 })
  vim.api.nvim_feedkeys('%', 'mx', false)
]])
expect(lua('return vim.api.nvim_win_get_cursor(0)[2]') == 5,
  '% stopped working after the config reset the runtimepath')
expect(lua("return vim.o.runtimepath:find('matchit', 1, true) ~= nil"),
  "matchit's directory was not put back on the runtimepath")
expect(lua("return vim.o.runtimepath:find(vim.fn.stdpath('config'), 1, true) == 1"),
  'restored directories went ahead of what the config put first')

-- typing keys: the config's own global mapping is claimed...
expect(claimed(lua, '<CR>'), "init.lua's global imap <CR> was not claimed")
expect(claimed(lua, '<Left>'), "init.lua's imap <Left> was not claimed")

-- ...a plugin's are not: lazily, buffer-local (autopairs' <BS>) or global
-- (cmp's <Tab>), with the pushes a lazy load triggers
lua([[
  vim.keymap.set('i', '<BS>', '<BS>', { buffer = true })
  vim.keymap.set('i', '<Tab>', function() end)
  vim.keymap.set('i', '<Del>', '<Del>')
  vim.keymap.set('i', '<F5>', '<Esc>')
  vim.cmd('doautocmd BufEnter')
]])
expect(not claimed(lua, '<BS>'), 'a later buffer-local imap <BS> was claimed')
expect(not claimed(lua, '<Tab>'), 'a later global imap <Tab> was claimed')
expect(not claimed(lua, '<Del>'), 'a later global imap <Del> was claimed')
expect(claimed(lua, '<F5>'), 'a later imap on a non-typing key must still be claimed')
expect(claimed(lua, '<CR>'), "the config's <CR> claim was lost on a later push")

-- an explicit refresh is the user asking
lua('vsneo.keymaps_refresh()')
expect(claimed(lua, '<Del>'), 'keymaps_refresh() did not take the new global imap <Del>')
expect(not claimed(lua, '<BS>'), 'keymaps_refresh() claimed a buffer-local typing key')

------------------------------------------------------------------
-- no init file: the rc pair, as before
------------------------------------------------------------------
lua = start('legacy', {
  ['.vsneorc'] = { 'let g:legacy_vim = 1' },
  ['.vsneorc.lua'] = { 'vim.g.legacy_lua = vim.g.legacy_vim + 1' },
})
expect(lua('return vim.g.legacy_lua') == 2, 'the ~/.vsneorc pair was not sourced in order')
expect(lua('return vim.env.MYVIMRC') == vim.NIL, '$MYVIMRC set without an init file')

------------------------------------------------------------------
-- init.vim counts too, and a throwing config does not stop the companion
------------------------------------------------------------------
lua = start('vimscript', {
  ['cfg/vsneo/init.vim'] = { 'let g:init_vim = 1', 'set wrap', 'call NoSuchFunction()' },
})
expect(lua('return vim.g.init_vim') == 1, 'init.vim was not sourced')
expect(lua('return vim.o.wrap') == false, 'forced options skipped after a failing config')
expect(lua("return type(vsneo.keymaps_refresh)") == 'function', 'the companion stopped at the failing config')

finish()
print('user_config_tests: ALL OK')
