-- Which-key mapping table: buffer-local mappings are included, <Plug> is
-- filtered, and the table refreshes on SourcePost (debounced), BufEnter,
-- and vsneo.keymaps_refresh().

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/keymaps.lua' })

local function find_mapping(lhs)
  for _, n in ipairs(t.reports(h, 'vsneo_keymaps')) do
    if n[2] == 'n' then
      for _, pair in ipairs(n[3]) do
        if pair[1] == lhs then return pair[2] end
      end
    end
  end
  return nil
end

-- nvim reports the leader expanded; the default leader is a backslash
t.eq(find_mapping('\\zz'), nil, 'test setup: unexpected mapping already present')

-- an interactive refresh picks up a new global mapping
vim.keymap.set('n', '<leader>zz', function() end, { desc = 'zz test' })
vsneo.keymaps_refresh()
t.eq(find_mapping('\\zz'), 'zz test', 'keymaps_refresh should include the new mapping')

-- buffer-local mappings ride the same push
vim.keymap.set('n', '<leader>ll', function() end, { desc = 'buffer local', buffer = true })
vsneo.keymaps_refresh()
t.eq(find_mapping('\\ll'), 'buffer local', 'buffer-local mapping missing from the table')

-- <Plug> is filtered, buffer-local included or not
vim.keymap.set('n', '<Plug>(testplug)', function() end, { buffer = true })
vsneo.keymaps_refresh()
t.eq(find_mapping('<Plug>(testplug)'), nil, '<Plug> mapping leaked into the table')

-- SourcePost refreshes, debounced
vim.keymap.set('n', '<leader>ss', function() end, { desc = 'source test' })
t.clear(h)
vim.cmd('doautocmd SourcePost')
local ok = vim.wait(1500, function()
  return find_mapping('\\ss') ~= nil
end, 50)
t.expect(ok, 'SourcePost did not refresh the table with the new mapping')

-- BufEnter refreshes too
vim.keymap.set('n', '<leader>bb', function() end, { desc = 'bufenter test' })
t.clear(h)
vim.cmd('doautocmd BufEnter')
t.eq(find_mapping('\\bb'), 'bufenter test', 'BufEnter did not refresh the table')

print('keymaps_tests: ALL OK')
