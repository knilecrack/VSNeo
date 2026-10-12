-- The :q/:w/:undo families route to Visual Studio through user commands, so
-- a bang, an argument or a | chain parses as Ex syntax instead of being
-- swallowed by :lua (E5107). g-/g+/U and :undo/:earlier/:later must never
-- reach nvim's undo tree.
--
-- Driven like production: a child nvim over RPC, keys through nvim_input. A
-- feedkeys harness does not expand the abbreviation before '!' and runs the
-- real :q!, which exits the test.

local companion = vim.fn.fnamemodify('VSNeo_Extension/Lua/vsneo.lua', ':p')
local chan = vim.fn.jobstart({ vim.v.progpath, '--embed', '--headless', '-u', 'NONE', '-i', 'NONE' },
  { rpc = true })
assert(chan > 0, 'could not start child nvim')

local function req(method, ...) return vim.rpcrequest(chan, method, ...) end
local function lua(code, ...) return req('nvim_exec_lua', code, { ... }) end

-- Capture the companion's VS commands in the child.
lua([[
  _G.sent = {}
  vim.rpcnotify = function(_, method, name)
    if method == 'vsneo_action' then table.insert(_G.sent, name) end
  end
  assert(loadfile(...))(0)
]], companion)

local function fail(msg) vim.fn.jobstop(chan); error('FAILED: ' .. msg, 2) end

local function run(keys, expected)
  lua('_G.sent = {}; vim.v.errmsg = ""')
  req('nvim_input', keys)
  local got = lua('vim.wait(20); return { table.concat(_G.sent, ","), vim.v.errmsg }')
  if got[2] ~= '' then fail(keys .. ' errored: ' .. got[2]) end
  if got[1] ~= expected then
    fail(keys .. ' sent "' .. got[1] .. '", expected "' .. expected .. '"')
  end
end

run(':q<CR>', 'Window.CloseDocumentWindow')
run(':q!<CR>', 'Window.CloseDocumentWindow')
run(':wq!<CR>', 'File.SaveSelectedItems,Window.CloseDocumentWindow')
run(':x!<CR>', 'File.SaveSelectedItems,Window.CloseDocumentWindow')
run(':qa!<CR>', 'File.Exit')
run(':w<CR>', 'File.SaveSelectedItems')
run(':w foo<CR>', 'File.SaveSelectedItems')
run(':w | q<CR>', 'File.SaveSelectedItems,Window.CloseDocumentWindow')
run(':w | e foo.txt<CR>', 'File.SaveSelectedItems,File.OpenFile')
run('g-', 'Edit.Undo')
run('g+', 'Edit.Redo')
run('U', 'Edit.Undo')
run(':undo<CR>', 'Edit.Undo')
run(':redo<CR>', 'Edit.Redo')
run(':earlier 10m<CR>', 'Edit.Undo')
run(':later 3<CR>', 'Edit.Redo')

-- a q later in the line is text, not the quit command
req('nvim_buf_set_lines', 0, 0, -1, false, { 'aqa' })
run(':1s/q/x/<CR>', '')
local line = req('nvim_buf_get_lines', 0, 0, 1, false)[1]
if line ~= 'axa' then fail(':s/q/x/ should substitute, got ' .. line) end

-- nor an escaped bar inside a pattern
req('nvim_buf_set_lines', 0, 0, -1, false, { 'aqa' })
run([[:1s/a\|q/x/g<CR>]], '')
line = req('nvim_buf_get_lines', 0, 0, 1, false)[1]
if line ~= 'xxx' then fail('escaped-bar :s should substitute, got ' .. line) end

vim.fn.jobstop(chan)
print('cmd_abbrev_tests: ALL OK')
