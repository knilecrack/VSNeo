-- A push made while an operator waits for its motion must say so. Autocmds
-- that run then (CursorMoved, which the viewport sync triggers a moment after
-- `c`/`d` when the showcmd margin resizes the view) read the mode as plain
-- 'n', and the extension's mode cache followed: a click took the plain-click
-- path instead of cancelling the operator, and `k` then ran it over a range
-- nobody typed. Driven over RPC against a child nvim, as production does.

local companion = vim.fn.fnamemodify('VSNeo_Extension/Lua/vsneo.lua', ':p')
local chan = vim.fn.jobstart({ vim.v.progpath, '--embed', '--headless', '-u', 'NONE', '-i', 'NONE' },
  { rpc = true })
assert(chan > 0, 'could not start child nvim')

local function req(method, ...) return vim.rpcrequest(chan, method, ...) end
local function lua(code, ...) return req('nvim_exec_lua', code, { ... }) end

lua([[
  _G.states = {}
  vim.rpcnotify = function(_, method, mode)
    if method == 'vsneo_state' then table.insert(_G.states, mode) end
  end
  assert(loadfile(...))(0)
  vim.api.nvim_buf_set_lines(0, 0, -1, false, { 'one', 'two', 'three', 'four' })
  vim.api.nvim_win_set_cursor(0, { 3, 0 })
  _G.states = {}
]], companion)

local function fail(msg) vim.fn.jobstop(chan); error('FAILED: ' .. msg, 2) end
local function last_state() return lua('return _G.states[#_G.states]') end

req('nvim_input', 'c')
lua('vim.wait(40)')
if last_state() ~= 'no' then fail('state after c should be no, got ' .. tostring(last_state())) end

-- The viewport sync's cursor move, while the operator is still pending.
lua('_G.states = {}; vim.api.nvim_win_set_cursor(0, { 3, 2 })')
lua('vim.wait(40)')
local seen = lua('return _G.states')
if #seen == 0 then fail('moving the cursor pushed no state') end
for _, m in ipairs(seen) do
  if m ~= 'no' then fail('a push during the pending operator said ' .. tostring(m)) end
end

-- Leaving the operator clears it: the next push is plain normal mode again.
req('nvim_input', '<Esc>')
lua('vim.wait(40)')
lua('_G.states = {}; vim.api.nvim_win_set_cursor(0, { 2, 0 })')
lua('vim.wait(40)')
if last_state() ~= 'n' then fail('after <Esc> the state should be n, got ' .. tostring(last_state())) end

vim.fn.jobstop(chan)
print('op_pending_state_tests: ALL OK')
