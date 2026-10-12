-- f/F/t/T labels (and the s jump) stand down inside macros. Replaying, the
-- label prompt ate the register's next key as a pick: fxdw on a line with
-- several x landed on the wrong x and w ran as a motion. Recording falls
-- back to the native motion too, so the register holds keys replay
-- reproduces. Driven over RPC like macro_insert_tests: keys typed with
-- nvim_input are what a register records.

local companion = vim.fn.fnamemodify('VSNeo_Extension/Lua/vsneo.lua', ':p')
local chan = vim.fn.jobstart({ vim.v.progpath, '--embed', '--headless', '-u', 'NONE', '-i', 'NONE' },
  { rpc = true })
assert(chan > 0, 'could not start child nvim')

local function req(method, ...) return vim.rpcrequest(chan, method, ...) end
local function lua(code, ...) return req('nvim_exec_lua', code, { ... }) end
lua([[
  vim.rpcnotify = function() end
  assert(loadfile(...))(0)
]], companion)

local function fail(msg) vim.fn.jobstop(chan); error('FAILED: ' .. msg, 2) end
local function eq(actual, expected, msg)
  if actual ~= expected then
    fail(msg .. ' (expected ' .. vim.inspect(expected) .. ', got ' .. vim.inspect(actual) .. ')')
  end
end
local function keys(k) req('nvim_input', k); lua('vim.wait(40)') end
local function lines() return req('nvim_buf_get_lines', 0, 0, -1, false) end

req('nvim_buf_set_lines', 0, 0, -1, false, { 'a x1 x2 x3', 'b x1 x2 x3' })
req('nvim_win_set_cursor', 0, { 1, 0 })
for _, k in ipairs({ 'qa', 'fx', 'dw', 'q' }) do keys(k) end
eq(lines()[1], 'a x2 x3', 'recording fxdw deletes at the first x')
-- The <Ignore> between chunks is the harness's: each settle request lands
-- as one while recording, f unmapped included.
eq(lua('return (vim.fn.keytrans(vim.fn.getreg("a")):gsub("<Ignore>", ""))'), 'fxdw',
  'register holds the native keys, no label')

req('nvim_win_set_cursor', 0, { 2, 0 })
keys('@a')
eq(lines()[2], 'b x2 x3', '@a replays fxdw on the first x')

-- backward, counted: 2Fx from the end lands on the second x back
req('nvim_buf_set_lines', 0, 0, -1, false, { 'x1 x2 x3 e', 'x1 x2 x3 e' })
req('nvim_win_set_cursor', 0, { 1, 9 })
for _, k in ipairs({ 'qb', '2Fx', 'D', 'q' }) do keys(k) end
eq(lines()[1], 'x1 ', 'recording 2FxD')
req('nvim_win_set_cursor', 0, { 2, 9 })
keys('@b')
eq(lines()[2], 'x1 ', '@b replays 2FxD')

-- replay alone (a register set, not recorded): the labels used to eat d
req('nvim_buf_set_lines', 0, 0, -1, false, { 'c x1 x2 x3' })
req('nvim_win_set_cursor', 0, { 1, 0 })
lua('vim.fn.setreg("c", "fxdw")')
keys('@c')
eq(lines()[1], 'c x2 x3', '@c of a set register replays fxdw natively')

vim.fn.jobstop(chan)
print('jump_macro_tests: ALL OK')
