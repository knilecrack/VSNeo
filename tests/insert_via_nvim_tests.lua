-- vsneo_insert_via_nvim: insert-mode typing reaches nvim as keys, so '.' and
-- macros are nvim's own. The companion must notice that and stand its
-- reconstruction down: a recorded register keeps the real keys (not a
-- <C-r><C-o>= rewrite), and '.' repeats natively. When Visual Studio wrote
-- part of the insert anyway (a completion commit), the reconstruction is
-- still the one that knows the text.
--
-- Same child-nvim harness as macro_insert_tests.lua: typed keys go in with
-- nvim_input, Visual Studio text with nvim_buf_set_text plus a cursor move.

local companion = vim.fn.fnamemodify('VSNeo_Extension/Lua/vsneo.lua', ':p')
local chan = vim.fn.jobstart({ vim.v.progpath, '--embed', '--headless', '-u', 'NONE', '-i', 'NONE' },
  { rpc = true })
assert(chan > 0, 'could not start child nvim')

local function req(method, ...) return vim.rpcrequest(chan, method, ...) end
local function lua(code, ...) return req('nvim_exec_lua', code, { ... }) end

-- Notifications are kept in the child for the switch's assertions.
lua([[
  _G.sent = {}
  vim.rpcnotify = function(_, method, ...) _G.sent[#_G.sent + 1] = { method, ... } end
  assert(loadfile(...))(0)
]], companion)

local function fail(msg) vim.fn.jobstop(chan); error('FAILED: ' .. msg, 2) end
local function eq(actual, expected, msg)
  if actual ~= expected then
    fail(msg .. ' (expected ' .. vim.inspect(expected) .. ', got ' .. vim.inspect(actual) .. ')')
  end
end

local function settle() lua('vim.wait(40)') end
local function keys(k) req('nvim_input', k); settle() end
local function vs(text)
  lua([[
    local text = ...
    local cur = vim.api.nvim_win_get_cursor(0)
    local parts = vim.split(text, '\n', { plain = true })
    vim.api.nvim_buf_set_text(0, cur[1] - 1, cur[2], cur[1] - 1, cur[2], parts)
    local row = cur[1] + #parts - 1
    local col = (#parts == 1) and cur[2] + #parts[#parts] or #parts[#parts]
    vim.api.nvim_win_set_cursor(0, { row, col })
  ]], text)
  settle()
end
local function run(seq)
  for _, item in ipairs(seq) do
    if type(item) == 'table' then vs(item.vs) else keys(item) end
  end
end
local function lines() return req('nvim_buf_get_lines', 0, 0, -1, false) end
local function reg(r) return lua('return vim.fn.getreg(...)', r) end
local function scene(new_lines, row, col)
  req('nvim_buf_set_lines', 0, 0, -1, false, new_lines)
  req('nvim_win_set_cursor', 0, { row or 1, col or 0 })
end
local function last_switch()
  return lua([[
    for i = #_G.sent, 1, -1 do
      if _G.sent[i][1] == 'vsneo_insert_via_nvim' then return _G.sent[i][2] end
    end
  ]])
end

-- 0. The switch: off by default; on/off/toggle set the global; 'buffer'
-- overrides it for one buffer; a filetype list turns it on per filetype.
eq(last_switch(), 0, 'off by default')
lua('vim.cmd("VSNeoInsertViaNvim on")')
eq(last_switch(), 1, ':VSNeoInsertViaNvim on')
lua('vim.cmd("VSNeoInsertViaNvim toggle")')
eq(last_switch(), 0, 'toggle flips it')
lua('vim.cmd("VSNeoInsertViaNvim")')
eq(last_switch(), 0, 'no argument only reports')
lua([[
  _G.first = vim.api.nvim_get_current_buf()
  _G.other = vim.api.nvim_create_buf(true, false)
  vim.b[_G.other].vsneo_insert_via_nvim = true
  vim.api.nvim_set_current_buf(_G.other)
]])
eq(last_switch(), 1, 'buffer override on entry')
lua('vim.api.nvim_set_current_buf(_G.first)')
eq(last_switch(), 0, 'back to the global in another buffer')
lua('vim.cmd("VSNeoInsertViaNvim buffer on")')
eq(last_switch(), 1, ':VSNeoInsertViaNvim buffer on')
lua('vim.cmd("VSNeoInsertViaNvim buffer clear")')
eq(last_switch(), 0, 'buffer clear falls back to the global')

-- Filetype list: on for markdown, off for cs, decided when the filetype lands.
lua([[
  vim.g.vsneo_insert_via_nvim = { 'markdown', 'text' }
  vim.bo.filetype = 'cs'
]])
eq(last_switch(), 0, 'cs is not in the list')
lua('vim.bo.filetype = "markdown"')
eq(last_switch(), 1, 'markdown is')
lua('vim.api.nvim_set_current_buf(_G.other)')   -- buffer override still wins
eq(last_switch(), 1, 'buffer variable wins over the list')
lua([[
  vim.b[_G.other].vsneo_insert_via_nvim = { 'yaml' }
  vim.cmd('doautocmd BufEnter')
]])
eq(last_switch(), 0, 'a buffer list is matched against the buffer filetype')
lua('vim.api.nvim_set_current_buf(_G.first)')
lua('vim.g.vsneo_insert_via_nvim = true')

-- 1. Typed through nvim: the register keeps the real keys.
scene({ 'one two three', 'four five six' })
run({ 'qa', 'cw', 'NEW', '<Esc>', 'q' })
eq(lines()[1], 'NEW two three', 'recorded change')
eq(reg('a'), 'cwNEW\27', 'register is nvim\'s own, not rewritten')
req('nvim_win_set_cursor', 0, { 2, 0 })
run({ '@a' })
eq(lines()[2], 'NEW five six', '@a replays')

-- 2. Insert-mode keys a slice cannot express survive in the register.
lua('vim.fn.setreg("r", "REG")')
scene({ 'x' })
run({ 'qb', 'A', ' <C-r>r', '<Esc>', 'q' })
eq(lines()[1], 'x REG', '<C-r>r inserted the register')
eq(reg('b'), 'A \18r\27', 'register keeps <C-r>r')

-- 3. '.' after typing through nvim, multi-line included.
scene({ 'aa bb', 'cc dd' })
run({ 'cw', 'X<CR>Y', '<Esc>' })
eq(lines()[1], 'X', 'line split typed through nvim')
eq(lines()[2], 'Ybb', 'second half (nvim drops the space at the break)')
req('nvim_win_set_cursor', 0, { 3, 0 })
run({ '.' })
eq(lines()[3], 'X', '. repeats the split')
eq(lines()[4], 'Ydd', '. repeats the text after it')

-- 4. Visual Studio wrote part of it (a completion commit): reconstructed.
scene({ 'p q', 'r s' })
run({ 'cw', 'Co', { vs = 'nsole' }, '<Esc>' })
eq(lines()[1], 'Console q', 'mixed insert')
req('nvim_win_set_cursor', 0, { 2, 0 })
run({ '.' })
eq(lines()[2], 'Console s', '. repeats the whole word, committed part included')

scene({ 'k' })
run({ 'qc', 'A', 'a', { vs = 'b' }, '<Esc>', 'q' })
eq(lines()[1], 'kab', 'mixed recording')
eq(reg('c'), 'A\18\15="ab"\r\27', 'mixed session falls back to the rewrite')

-- 5. A Visual Studio edit ending at the cursor (a completion commit) leaves
-- nvim's cursor after it, as typing would: the next routed key lands there.
scene({ 'x' })
run({ 'A', ' neo' })
lua([[
  local cur = vim.api.nvim_win_get_cursor(0)
  vsneo.apply_spans(0, { { 0, cur[2] - 3, 0, cur[2], { 'neovim' } } })
]])
run({ 'Z', '<Esc>' })
eq(lines()[1], 'x neovimZ', 'typed after the committed word, not inside it')

-- An edit elsewhere on the line leaves the cursor alone.
scene({ 'ab' })
run({ 'A', 'c' })
lua('vsneo.apply_spans(0, { { 0, 0, 0, 0, { "<" } } })')
run({ 'd', '<Esc>' })
eq(lines()[1], '<abcd', 'unrelated edit: typing continues where it was')

vim.fn.jobstop(chan)
print('insert_via_nvim_tests: ALL OK')
