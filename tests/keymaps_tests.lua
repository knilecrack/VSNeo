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

-- a buffer-local mapping shadows a global with the same lhs (as nvim itself
-- does): the extension dedupes the table first-wins, so the buffer-local
-- pair - and its desc - must come first
vim.keymap.set('n', '<leader>dd', function() end, { desc = 'global dd' })
vim.keymap.set('n', '<leader>dd', function() end, { desc = 'buffer dd', buffer = true })
vsneo.keymaps_refresh()
t.eq(find_mapping('\\dd'), 'buffer dd',
  'buffer-local desc should shadow the global for the same lhs')

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

------------------------------------------------------------------
-- vsneo_imaps: the user's insert mappings on named keys, so the
-- extension knows which keys it may claim while in insert mode.
------------------------------------------------------------------

local function find_imap(lhs)
  for _, n in ipairs(t.reports(h, 'vsneo_imaps')) do
    for _, l in ipairs(n[2]) do
      if l == lhs then return true end
    end
  end
  return false
end

-- nvim's own defaults are filtered: <C-W> and <Tab> ship with nvim and
-- must never be claimed from Visual Studio for every user
vsneo.keymaps_refresh()
t.expect(not find_imap('<C-W>'), 'default <C-W> insert mapping leaked into vsneo_imaps')
t.expect(not find_imap('<Tab>'), 'default <Tab> insert mapping leaked into vsneo_imaps')

-- a user mapping on a named key is pushed
vim.keymap.set('i', '<Left>', '<Esc>')
vsneo.keymaps_refresh()
t.expect(find_imap('<Left>'), 'user imap <Left> missing from vsneo_imaps')

-- buffer-local insert mappings ride the same push
vim.keymap.set('i', '<Right>', '<Esc><Right>', { buffer = true })
vsneo.keymaps_refresh()
t.expect(find_imap('<Right>'), 'buffer-local imap <Right> missing from vsneo_imaps')

-- a printable lhs arrives in Visual Studio as text, not as a key, so no
-- interception point could ever claim it - it must not be pushed
vim.keymap.set('i', 'jk', '<Esc>')
vsneo.keymaps_refresh()
t.expect(not find_imap('jk'), 'printable lhs jk leaked into vsneo_imaps')

-- a default the user redefined counts as the user's own
vim.keymap.set('i', '<C-U>', '<Esc>')
vsneo.keymaps_refresh()
t.expect(find_imap('<C-U>'), 'redefined default <C-U> missing from vsneo_imaps')

print('keymaps_tests: ALL OK')
