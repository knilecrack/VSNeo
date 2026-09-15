-- Shared harness for the companion test suites.
--
-- Every suite is a standalone script run by tests/run-tests.ps1 as
--   nvim --headless -u NONE -i NONE -l tests/<name>_tests.lua
-- from the repo root (the companion is loaded by relative path). The RPC
-- channel is stubbed: notifications are captured for assertions, and any
-- rpcrequest is a bug in the test or the companion - the extension is not
-- here to answer.
--
-- The runner isolates HOME/USERPROFILE, so the companion's rc sourcing at
-- load time finds nothing and every suite runs against the stock companion.

local M = {}

function M.setup(opts)
  opts = opts or {}

  local notifications = {}
  vim.rpcnotify = function(chan, method, ...)
    notifications[#notifications + 1] = { method, ... }
  end
  vim.rpcrequest = function(...)
    error('tests must not rpcrequest; the extension is not here to answer', 2)
  end

  local lines = opts.lines
  if not lines then
    lines = {}
    for i = 1, 100 do lines[i] = 'line ' .. i end
  end
  vim.api.nvim_buf_set_lines(0, 0, -1, false, lines)

  -- Forward slashes keep the tests OS-agnostic; for_current_buffer
  -- normalizes before comparing.
  local name = opts.name or 'C:/test/buffer.lua'
  vim.api.nvim_buf_set_name(0, name)

  local chunk, err = loadfile('VSNeo_Extension/Lua/vsneo.lua')
  assert(chunk, err)
  local ok, load_err = pcall(chunk, 1)
  assert(ok, load_err)

  return { notifications = notifications, name = name }
end

function M.expect(cond, msg)
  if not cond then error('FAILED: ' .. msg, 2) end
end

function M.eq(actual, expected, msg)
  if actual ~= expected then
    error('FAILED: ' .. msg .. ' (expected ' .. tostring(expected)
      .. ', got ' .. tostring(actual) .. ')', 2)
  end
end

--- Last notification with the given method, or nil. Payload starts at [2].
function M.report(handle, method)
  local found = nil
  for _, n in ipairs(handle.notifications) do
    if n[1] == method then found = n end
  end
  return found
end

--- All notifications with the given method.
function M.reports(handle, method)
  local out = {}
  for _, n in ipairs(handle.notifications) do
    if n[1] == method then out[#out + 1] = n end
  end
  return out
end

function M.clear(handle)
  while #handle.notifications > 0 do table.remove(handle.notifications) end
end

return M
