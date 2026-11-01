-- :Vsc completion with the cursor inside the command name and an argument
-- after it. nvim then passes the rest of the line as the lead, so the
-- completion must read the name off the text before the cursor.
--
-- getcompletion() always completes at the end of the line, so this types
-- into a child nvim's command line over RPC, as the extension does.

local companion = vim.fn.fnamemodify('VSNeo_Extension/Lua/vsneo.lua', ':p')
local chan = vim.fn.jobstart({ vim.v.progpath, '--embed', '--headless', '-u', 'NONE', '-i', 'NONE' },
  { rpc = true })
assert(chan > 0, 'could not start child nvim')

local function req(method, ...) return vim.rpcrequest(chan, method, ...) end
local function fail(msg) vim.fn.jobstop(chan); error('FAILED: ' .. msg, 2) end

req('nvim_exec_lua', [[
  vim.rpcnotify = function() end
  assert(loadfile(...))(0)
  vsneo._set_vs_commands({ 'Edit.FormatDocument', 'Debug.Start' })
]], { companion })

local function tab_at(keys, expected, what)
  req('nvim_input', '<Esc>' .. keys .. '<Tab>')
  vim.wait(200)
  local line = req('nvim_call_function', 'getcmdline', {})
  if line ~= expected then fail(what .. ' (expected "' .. expected .. '", got "' .. line .. '")') end
end

tab_at(':Vsc Edit.F', 'Vsc Edit.FormatDocument', 'end of line')
tab_at(':Vsc Edit.F file.cs' .. string.rep('<Left>', 8),
  'Vsc Edit.FormatDocument file.cs', 'cursor in the name, argument after it')
tab_at(':Vsc Edit.F file.cs<End>', 'Vsc Edit.F file.cs', 'cursor in the argument: nothing completed')

vim.fn.jobstop(chan)
print('vsc_cursor_tests: ALL OK')
