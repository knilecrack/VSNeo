-- :VSNeoParsers - lists the treesitter parsers nvim finds, and builds missing
-- ones with git and the tree-sitter CLI into VSNeo's own folder. The
-- external tools are faked here (no network, no compiler); a real install was
-- verified by hand.

local t = dofile('tests/helper.lua')
t.setup({ name = 'C:/test/parsers.txt' })

-- Filetypes whose parser has another name are registered at load.
t.eq(vim.treesitter.language.get_lang('cs'), 'c_sharp', 'cs uses the c_sharp parser')
t.eq(vim.treesitter.language.get_lang('typescriptreact'), 'tsx', 'typescriptreact uses tsx')
t.eq(vim.treesitter.language.get_lang('javascriptreact'), 'javascript', 'javascriptreact uses javascript')
t.eq(vim.treesitter.language.get_lang('sh'), 'bash', 'sh uses bash')

local echoed = {}
vim.api.nvim_echo = function(chunks)
  local s = ''
  for _, c in ipairs(chunks) do s = s .. c[1] end
  echoed[#echoed + 1] = s
end
local function last_echo() return echoed[#echoed] or '' end

-- Listing: C ships with nvim, so it is found; a made-up grammar is missing.
vim.cmd('VSNeoParsers')
local listing = last_echo()
t.expect(listing:find('c%s+%(c%)%s+found') ~= nil, 'c is found (it ships with nvim): ' .. listing)
t.expect(listing:find('rust%s+%(rust%)%s+missing') ~= nil or listing:find('rust%s+%(rust%)%s+found') ~= nil,
  'rust is listed')
t.expect(listing:find('typescript') ~= nil and listing:find('tsx') ~= nil, 'typescript and tsx are listed')

-- Install: clone, then build into ~/.vsneo/pack/vsneo-parsers/start/parsers/parser.
local runs = {}
vim.fn.executable = function() return 1 end
vim.system = function(cmd, _, on_exit)
  runs[#runs + 1] = cmd
  on_exit({ code = 0, stdout = '', stderr = '' })
end
local added = {}
vim.treesitter.language.add = function(lang, opts)
  added[#added + 1] = { lang = lang, path = opts and opts.path }
  return true
end

local finished = false
vsneo.parsers.install('typescript', function(ok) finished = ok end)
vim.wait(2000, function() return finished end)
t.expect(finished, 'install reports success')
t.eq(runs[1][1], 'git', 'first, git')
t.eq(runs[1][2], 'clone', 'clones the grammar')
t.eq(runs[1][5], 'https://github.com/tree-sitter/tree-sitter-typescript', 'from the typescript repository')
t.eq(runs[2][1], 'tree-sitter', 'then tree-sitter')
t.eq(runs[2][2], 'build', 'builds')
local out = runs[2][4]
t.expect(out:find('vsneo%-parsers/start/parsers/parser/typescript%.') ~= nil, 'into VSNeo\'s parser folder: ' .. out)
t.expect(runs[2][5]:find('tree%-sitter%-typescript/typescript$') ~= nil, 'from the typescript folder of the repo')
t.eq(added[1].lang, 'typescript', 'and loads it')
t.eq(added[1].path, out, 'from where it was built')
local root = vim.fs.normalize(vim.fn.expand('~/.vsneo/pack/vsneo-parsers/start/parsers'))
local on_rtp = false
for _, p in ipairs(vim.opt.rtp:get()) do if vim.fs.normalize(p) == root then on_rtp = true end end
t.expect(on_rtp, 'VSNeo\'s parser folder is on the runtimepath')

-- tsx shares the repository; once cloned, it is only built.
runs = {}
finished = false
vim.fn.isdirectory = function() return 1 end
vsneo.parsers.install('tsx', function(ok) finished = ok end)
vim.wait(2000, function() return finished end)
t.eq(#runs, 1, 'a cloned repository is reused')
t.expect(runs[1][5]:find('tree%-sitter%-typescript/tsx$') ~= nil, 'tsx from its own folder')

-- Failures say why and report false.
vim.system = function(cmd, _, on_exit) on_exit({ code = 1, stdout = '', stderr = 'no C compiler' }) end
local result = nil
vsneo.parsers.install('rust', function(ok) result = ok end)
vim.wait(2000, function() return result ~= nil end)
t.eq(result, false, 'a failed build reports false')
vim.wait(200, function() return last_echo():find('no C compiler') ~= nil end)
t.expect(last_echo():find('no C compiler') ~= nil, 'and says why: ' .. last_echo())

result = nil
vsneo.parsers.install('cobol', function(ok) result = ok end)
t.eq(result, false, 'an unknown language reports false')

vim.fn.executable = function(tool) return tool == 'git' and 1 or 0 end
result = nil
vsneo.parsers.install('go', function(ok) result = ok end)
t.eq(result, false, 'a missing tree-sitter CLI reports false')

print('parsers_tests: ALL OK')
