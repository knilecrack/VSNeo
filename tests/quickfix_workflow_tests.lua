local t = dofile('tests/helper.lua')
t.setup({ lines = { 'local first = "OldName"' }, name = 'C:/test/quickfix.lua' })

local current = vim.api.nvim_get_current_buf()
local name = vim.api.nvim_buf_get_name(current)
local function entry(lnum, text)
  return { filename = name, lnum = lnum, col = 1, text = text }
end

-- The host's new-list request uses nr='$': it must push a list, not replace the current one.
vim.fn.setqflist({}, ' ', { title = 'first search', items = { entry(1, 'first') } })
vim.fn.setqflist({}, ' ', { nr = '$', title = 'second search', items = { entry(1, 'second') } })
t.eq(vim.fn.getqflist({ nr = 0 }).nr, 2, 'new export pushes quickfix history')
t.eq(vim.fn.getqflist({ title = true }).title, 'second search', 'new list is current')
vim.cmd('colder')
t.eq(vim.fn.getqflist({ title = true }).title, 'first search', 'colder returns to the previous export')
vim.cmd('cnewer')
t.eq(vim.fn.getqflist({ title = true }).title, 'second search', 'cnewer returns to the new export')

-- Append adds locations without replacing the current list or its title.
vim.fn.setqflist({}, 'a', { items = { entry(1, 'appended') } })
t.eq(#vim.fn.getqflist(), 2, 'append adds to the current list')
t.eq(vim.fn.getqflist({ title = true }).title, 'second search', 'append preserves the title')

-- Cfilter refines the exported locations without another search.
vim.fn.setqflist({}, ' ', {
  title = 'filter',
  items = { entry(1, 'keep Important'), entry(1, 'drop other') },
})
vim.cmd('Cfilter Important')
local filtered = vim.fn.getqflist()
t.eq(#filtered, 1, 'Cfilter keeps matching entries')
t.eq(filtered[1].text, 'keep Important', 'Cfilter keeps the requested entry')
t.eq(vim.fn.getqflist({ title = true }).title, 'filter', 'Cfilter preserves the list title')
vim.cmd('colder')
t.eq(#vim.fn.getqflist(), 2, 'Cfilter preserves the unfiltered list in history')
vim.cmd('cnewer')

vim.fn.setqflist({}, ' ', {
  title = 'filter bang',
  items = { entry(1, 'drop Important'), entry(1, 'keep other') },
})
vim.cmd('Cfilter! Important')
filtered = vim.fn.getqflist()
t.eq(#filtered, 1, 'Cfilter! rejects matching entries')
t.eq(filtered[1].text, 'keep other', 'Cfilter! keeps non-matching entries')

-- cfdo operates on each distinct file buffer; edits stay in memory, as they do in VS.
local second = vim.api.nvim_create_buf(true, false)
vim.api.nvim_buf_set_name(second, 'C:/test/quickfix-second.lua')
vim.api.nvim_buf_set_lines(second, 0, -1, false, { 'local second = "OldName"' })
vim.fn.setqflist({}, ' ', {
  title = 'batch',
  items = {
    { bufnr = current, lnum = 1, col = 1, text = 'first' },
    { bufnr = second, lnum = 1, col = 1, text = 'second' },
  },
})
vim.cmd('cfdo %s/OldName/NewName/ge')
t.eq(vim.api.nvim_buf_get_lines(current, 0, 1, false)[1], 'local first = "NewName"', 'cfdo edits the first buffer')
t.eq(vim.api.nvim_buf_get_lines(second, 0, 1, false)[1], 'local second = "NewName"', 'cfdo edits the second buffer')
t.eq(vim.bo[current].modified, true, 'batch edits are not saved automatically')

print('quickfix_workflow_tests: ALL OK')
