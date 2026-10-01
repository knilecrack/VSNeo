-- vsneo.set_shada: a shada file per solution. The path arrives from the
-- extension on solution open/close; the companion switches 'shadafile',
-- reads the file, and writes it on a timer, on BufLeave and when the
-- solution closes - nvim itself is killed at shutdown and never writes one.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/shada.lua' })

t.eq(vim.o.shadafile, 'NONE', 'starts with -i NONE')

local dir = vim.fn.tempname()
local path = dir .. '/.vs/vsneo.shada'

t.expect(vsneo.set_shada(path), 'a solution path is accepted')
t.eq(vim.o.shadafile, path, 'shadafile follows the solution')
t.expect(vim.fn.isdirectory(dir .. '/.vs') == 1, 'the .vs directory is created')

-- Something worth remembering, then the solution closes.
vim.fn.setreg('q', 'remember me')
t.expect(vsneo.set_shada(''), 'no solution is accepted')
t.eq(vim.o.shadafile, 'NONE', 'back to -i NONE without a solution')
t.expect(vim.fn.filereadable(path) == 1, 'closing the solution wrote the file')

-- A fresh session on the same solution reads it back.
vim.fn.setreg('q', '')
vsneo.set_shada(path)
t.eq(vim.fn.getreg('q'), 'remember me', 'reopening the solution restores the register')

-- Opt-out keeps -i NONE.
vsneo.set_shada('')
vim.g.vsneo_shada = false
t.expect(not vsneo.set_shada(path), 'vsneo_shada = false declines')
t.eq(vim.o.shadafile, 'NONE', 'and leaves shadafile alone')
vim.g.vsneo_shada = nil

vim.fn.delete(dir, 'rf')
print('shada_tests: ALL OK')
