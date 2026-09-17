-- The vsneo_recording contract: RecordingEnter pushes the register being
-- recorded into, RecordingLeave pushes ''. The leave side must not read
-- reg_recording(): nvim fires RecordingLeave before clearing the register
-- when keys arrive over nvim_input, which latched the badge on (the stop
-- looked impossible).

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/recording.lua' })

t.clear(h)
vim.fn.feedkeys('qa', 'x')
local enter = t.report(h, 'vsneo_recording')
t.expect(enter ~= nil, 'no vsneo_recording on RecordingEnter')
t.eq(enter[2], 'a', 'RecordingEnter should push the register being recorded into')

vim.fn.feedkeys('q', 'x')
t.eq(vim.fn.reg_recording(), '', 'recording should be stopped')
local leave = t.report(h, 'vsneo_recording')
t.expect(leave ~= nil, 'no vsneo_recording on RecordingLeave')
t.eq(leave[2], '', 'RecordingLeave must push an empty register')

-- and the badge comes back for a different register
t.clear(h)
vim.fn.feedkeys('qz', 'x')
enter = t.report(h, 'vsneo_recording')
t.expect(enter ~= nil, 'no vsneo_recording on the second RecordingEnter')
t.eq(enter[2], 'z', 'RecordingEnter should push the new register')
vim.fn.feedkeys('q', 'x')

print('recording_tests: ALL OK')
