-- nvim's highlighting is never shown (Visual Studio draws the text), so the
-- companion turns regex syntax and Treesitter off - without touching
-- filetype detection or indent - unless g:vsneo_syntax opts back in.

local t = dofile('tests/helper.lua')
t.setup({ name = 'C:/test/syntax.lua' })

local function open(ext, body)
  local path = vim.fn.tempname() .. ext
  vim.fn.writefile({ body }, path)
  vim.cmd('edit ' .. vim.fn.fnameescape(path))
  return vim.api.nvim_get_current_buf()
end

t.expect(vim.g.syntax_on == nil, 'syntax should be off after the companion loads')

local cs = open('.cs', 'class A {}')
t.eq(vim.bo[cs].filetype, 'cs', 'filetype detection still works')
t.eq(vim.bo[cs].syntax, '', 'no regex syntax for C#')
t.expect(vim.bo[cs].indentexpr ~= '' or vim.bo[cs].cindent, 'indent still set up')

local lua = open('.lua', 'local x = 1')
t.eq(vim.bo[lua].filetype, 'lua', 'lua filetype detected')
t.expect(not vim.treesitter.highlighter.active[lua], 'no Treesitter highlighting for Lua')
t.eq(vim.bo[lua].syntax, '', 'no legacy syntax left behind by stopping Treesitter')

-- Opt back in.
vim.g.vsneo_syntax = 1
local lua2 = open('.lua', 'local y = 2')
t.expect(vim.treesitter.highlighter.active[lua2] ~= nil, 'g:vsneo_syntax = 1 keeps Treesitter')

print('syntax_off_tests: ALL OK')
