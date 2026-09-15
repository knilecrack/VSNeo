-- Fold mirroring: folds_set/fold_closed/fold_opened application, echo
-- absorption, path guard, counts over closed folds, native z-command
-- detection (triples with existence), zf routing, and the round trip back.
--
-- nvim cannot close a one-line fold ('foldminlines'), so 60-60 rides through
-- several scenes as the open-but-existing case.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/fold.lua' })

local function folds_report()
  local n = t.report(h, 'vsneo_folds_changed')
  return n and n[2] or nil
end

local function fold_creates()
  local out = {}
  for _, n in ipairs(t.reports(h, 'vsneo_fold_create')) do
    out[#out + 1] = { n[2], n[3], n[4] }
  end
  return out
end

-- folds_set: one closed region, one open region
vsneo.folds_set('C:/test/fold.lua', { 10, 20, true, 30, 40, false })
t.eq(vim.fn.foldclosed(10), 10, 'fold 10-20 should be closed')
t.expect(vim.fn.foldlevel(30) > 0 and vim.fn.foldclosed(30) == -1,
  'fold 30-40 should exist but be open')

-- echo: identical set is a no-op (must not error or change state)
vsneo.folds_set('C:/test/fold.lua', { 10, 20, true, 30, 40, false })
t.eq(vim.fn.foldclosed(10), 10, 'echo folds_set changed state')

-- path guard: another document's state must be dropped
vsneo.folds_set('C:/other/file.lua', { 1, 5, true })
t.eq(vim.fn.foldclosed(1), -1, 'wrong-path folds_set was applied')

-- a 1-line region: nvim creates the fold but cannot close it
vsneo.folds_set('C:/test/fold.lua', { 10, 20, true, 30, 40, false, 60, 60, true })
t.eq(vim.fn.foldclosed(10), 10, 'fold 10-20 lost after 1-line folds_set')
t.expect(vim.fn.foldlevel(60) > 0, '1-line fold should exist')
t.eq(vim.fn.foldclosed(60), -1, '1-line fold cannot be closed')
-- ...and its agreed state records reality, so the next push sees no change
t.clear(h)
vim.cmd('doautocmd CursorMoved')
t.expect(folds_report() == nil, 'phantom report for the unclosable 1-line fold')

-- incremental close of the open fold, then its echo
vsneo.fold_closed('C:/test/fold.lua', 30, 40)
t.eq(vim.fn.foldclosed(30), 30, 'fold 30-40 should close via fold_closed')
vsneo.fold_closed('C:/test/fold.lua', 30, 40)
t.eq(vim.fn.foldclosed(30), 30, 'echo fold_closed changed state')

-- incremental open, then its echo
vsneo.fold_opened('C:/test/fold.lua', 30)
t.expect(vim.fn.foldclosed(30) == -1 and vim.fn.foldlevel(30) > 0,
  'fold 30-40 should reopen via fold_opened')
vsneo.fold_opened('C:/test/fold.lua', 30)
t.expect(vim.fn.foldclosed(30) == -1 and vim.fn.foldlevel(30) > 0,
  'echo fold_opened changed state')

-- counts treat the closed fold as one line: from line 5, 5j lands on the
-- fold (6,7,8,9,fold), the next 5j lands on 25 (21..25)
vim.api.nvim_win_set_cursor(0, { 5, 0 })
vim.cmd('normal! 5j')
t.eq(vim.api.nvim_win_get_cursor(0)[1], 10, '5j from line 5 should land on the fold')
vim.cmd('normal! 5j')
t.eq(vim.api.nvim_win_get_cursor(0)[1], 25, 'second 5j should land past the fold')

-- native zo is detected and reported as existence triples
t.clear(h)
vim.api.nvim_win_set_cursor(0, { 10, 0 })
vim.cmd('normal! zo')
vim.api.nvim_win_set_cursor(0, { 11, 0 })
vim.cmd('doautocmd CursorMoved')
local report = folds_report()
t.expect(report ~= nil, 'zo was not detected/reported')
t.expect(#report == 9 and report[3] == false and report[6] == false
    and report[9] == false,
  'report after zo should be all three folds open, got ' .. #report .. ' entries')

-- native zc on the open 30-40 fold
t.clear(h)
vim.api.nvim_win_set_cursor(0, { 35, 0 })
vim.cmd('normal! zc')
vim.cmd('doautocmd CursorMoved')
report = folds_report()
t.expect(report ~= nil, 'zc was not detected/reported')
t.expect(#report == 9 and report[4] == 30 and report[6] == true,
  'report after zc should have {30,40,true}')

-- zd deletes the fold under the cursor; it leaves the reported list entirely
t.clear(h)
vim.api.nvim_win_set_cursor(0, { 10, 0 })
vim.cmd('normal! zd')
vim.cmd('doautocmd CursorMoved')
report = folds_report()
t.expect(report ~= nil, 'zd was not detected/reported')
t.expect(#report == 6 and report[1] == 30 and report[3] == true
    and report[4] == 60,
  'report after zd should be {30,40,true,60,60,false}, got '
    .. #report .. ' entries')

-- Visual Studio resurrects a deleted fold with a full push
vsneo.folds_set('C:/test/fold.lua', { 10, 20, true, 30, 40, true, 60, 60, false })
t.eq(vim.fn.foldclosed(10), 10, 'folds_set did not resurrect the zd-deleted fold')

-- zR opens everything; detection reports it
t.clear(h)
vim.cmd('normal! zR')
vim.cmd('doautocmd CursorMoved')
report = folds_report()
t.expect(report ~= nil and #report == 9 and report[3] == false
    and report[6] == false and report[9] == false,
  'zR should report all folds open')

-- zf in visual mode: selects lines 80-82 (outside every existing fold),
-- reports the range to VS, and creates NO nvim-only fold
t.clear(h)
vim.api.nvim_win_set_cursor(0, { 80, 0 })
vim.fn.feedkeys('V2jzf', 'mx')
local creates = fold_creates()
t.eq(#creates, 1, 'visual zf should send one fold_create')
t.expect(creates[1][2] == 80 and creates[1][3] == 82,
  'visual zf range should be 80-82, got ' .. tostring(creates[1][2])
    .. '-' .. tostring(creates[1][3]))
t.eq(vim.fn.foldlevel(80), 0, 'visual zf created an nvim-only fold')

-- zf{motion} in normal mode: zf2j from line 50 covers 50-52
t.clear(h)
vim.api.nvim_win_set_cursor(0, { 50, 0 })
vim.fn.feedkeys('zf2j', 'mx')
creates = fold_creates()
t.eq(#creates, 1, 'zf2j should send one fold_create')
t.expect(creates[1][2] == 50 and creates[1][3] == 52,
  'zf2j range should be 50-52, got ' .. tostring(creates[1][2])
    .. '-' .. tostring(creates[1][3]))

-- the round trip: VS answers fold_create with fold_closed, creating the fold
vsneo.fold_closed('C:/test/fold.lua', 80, 82)
t.eq(vim.fn.foldclosed(80), 80, 'fold 80-82 should exist after the round trip')

-- The command-line guards (a folds_set mid-incsearch is skipped; the
-- note_viewport clamp is off) cannot be tested here: a headless -l script
-- never enters cmdline mode, feedkeys(':') included.

print('fold_tests: ALL OK')
