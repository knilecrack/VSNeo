-- Jump marking: vsneo.goto_cmd tags its vsneo_action with the 'jump' marker
-- (the extension records the caret's spot on JumpBackStack for <C-o>), while
-- plain vsneo.cmd carries no marker. goto_cmd also still writes nvim's
-- previous-context mark, so '' keeps working.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/jump.lua' })

local function last_action()
  return t.report(h, 'vsneo_action')
end

-- goto_cmd: marker present, m' written
vim.api.nvim_win_set_cursor(0, { 42, 0 })
t.clear(h)
vsneo.goto_cmd('Edit.GoToDefinition')
local action = last_action()
t.eq(action[2], 'Edit.GoToDefinition', 'goto_cmd should send the command name')
t.eq(action[3], '', 'goto_cmd with no args should send an empty argument string')
t.eq(action[4], 'jump', 'goto_cmd should tag the action as a jump')
local prev = vim.fn.getpos([['']])
t.eq(prev[2], 42, "goto_cmd should write the previous-context mark at the cursor's line")

-- goto_cmd with args: args ride along, marker still last
t.clear(h)
vsneo.goto_cmd('Edit.GoToDefinition', 'Some.Symbol')
action = last_action()
t.eq(action[3], 'Some.Symbol', 'goto_cmd should forward its args')
t.eq(action[4], 'jump', 'goto_cmd with args should still tag the action as a jump')

-- plain cmd: no marker, no mark written
vim.api.nvim_win_set_cursor(0, { 7, 0 })
t.clear(h)
vsneo.cmd('Build.BuildSolution')
action = last_action()
t.eq(action[2], 'Build.BuildSolution', 'cmd should send the command name')
t.eq(action[4], nil, 'plain cmd must not be tagged as a jump')
t.eq(vim.fn.getpos([['']])[2], 42, 'plain cmd must not write the previous-context mark')

print('jump_tests: OK')
