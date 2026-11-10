-- The syntax motions map n, x and o together, so a mapping the user made in
-- any of those modes (the rc runs first) keeps them out entirely - the check
-- used to look at normal mode only, and a user's xnoremap ]] was overwritten.

vim.keymap.set('x', ']]', 'j', { desc = 'user' })
vim.keymap.set('o', '[m', 'k', { desc = 'user' })

local t = dofile('tests/helper.lua')
t.setup({ name = 'C:/test/Mapping.cs' })

t.eq(vim.fn.maparg(']]', 'x', false, true).desc, 'user', "the user's xnoremap ]] survives")
t.eq(vim.fn.maparg(']]', 'n'), '', ']] is left alone in normal mode too')
t.eq(vim.fn.maparg('[m', 'o', false, true).desc, 'user', "the user's onoremap [m survives")
t.expect(vim.fn.maparg(']m', 'n') ~= '', 'an unmapped ]m still gets the syntax motion')

print('syntax_mapping_tests: ALL OK')
