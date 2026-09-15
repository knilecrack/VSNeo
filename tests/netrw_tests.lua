-- netrw routing: the :Explore family opens Visual Studio's Solution
-- Explorer (with sync-to-active-document), and :Edit on a directory is an
-- explorer request rather than a file open.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/netrw.lua' })

local function action_names()
  local out = {}
  for _, n in ipairs(t.reports(h, 'vsneo_action')) do
    out[#out + 1] = n[2]
  end
  return out
end

-- every variant routes to the Solution Explorer pair
for _, name in ipairs({ 'Explore', 'Ex', 'Vexplore', 'Sexplore', 'Hexplore', 'Texplore' }) do
  t.clear(h)
  vim.cmd(name)
  local actions = action_names()
  t.eq(#actions, 2, ':' .. name .. ' should send two VS commands')
  t.eq(actions[1], 'SolutionExplorer.SyncWithActiveDocument',
    ':' .. name .. ' should sync first')
  t.eq(actions[2], 'View.SolutionExplorer',
    ':' .. name .. ' should focus the Solution Explorer')
end

-- :Edit on a directory is an explorer request; on a file it is File.OpenFile
local dir = vim.fn.tempname()
vim.fn.mkdir(dir, 'p')
vim.fn.writefile({ 'x' }, dir .. '/real.txt')

t.clear(h)
vim.cmd('Edit ' .. dir)
local actions = action_names()
t.eq(#actions, 2, ':Edit on a directory should route to the Solution Explorer')
t.eq(actions[2], 'View.SolutionExplorer', ':Edit directory target')

t.clear(h)
vim.cmd('Edit ' .. dir .. '/real.txt')
actions = action_names()
t.eq(#actions, 1, ':Edit on a file should send one File.OpenFile')
t.eq(actions[1], 'File.OpenFile', ':Edit file target')

print('netrw_tests: ALL OK')
