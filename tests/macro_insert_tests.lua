-- Macros keep their inserted text. Insert mode is passthrough: text typed in
-- Visual Studio reaches nvim through the buffer mirror, never as keys, so a
-- register used to record "cw<Esc>" and replay deleted without inserting.
--
-- Driven exactly like production: a child nvim over RPC. Keys go in with
-- nvim_input (typed, and recorded, as the extension's keys are); Visual
-- Studio typing is nvim_buf_set_text plus a cursor move (the mirror and the
-- caret push), which never reaches a register. A feedkeys harness cannot
-- model this: mixing typed and untyped chunks in one typeahead marks the
-- wrong keys as typed.

local companion = vim.fn.fnamemodify('VSNeo_Extension/Lua/vsneo.lua', ':p')
local chan = vim.fn.jobstart({ vim.v.progpath, '--embed', '--headless', '-u', 'NONE', '-i', 'NONE' },
  { rpc = true })
assert(chan > 0, 'could not start child nvim')

local function req(method, ...) return vim.rpcrequest(chan, method, ...) end
local function lua(code, ...) return req('nvim_exec_lua', code, { ... }) end

-- The companion notifies channel 0 (broadcast); nothing here listens.
lua([[
  vim.rpcnotify = function() end
  assert(loadfile(...))(0)
]], companion)

local function fail(msg) vim.fn.jobstop(chan); error('FAILED: ' .. msg, 2) end
local function eq(actual, expected, msg)
  if actual ~= expected then
    fail(msg .. ' (expected ' .. vim.inspect(expected) .. ', got ' .. vim.inspect(actual) .. ')')
  end
end

-- Let the child drain its input and run scheduled callbacks. A regular
-- request is served only once pending input is processed; the wait inside
-- lets vim.schedule'd captures and the register rewrite run.
local function settle() lua('vim.wait(40)') end

local function keys(k)
  req('nvim_input', k)
  settle()
end

-- Visual Studio typing: the mirror writes the text at the cursor, the caret
-- push moves nvim's cursor to its end.
local function vs(text)
  lua([[
    local text = ...
    local cur = vim.api.nvim_win_get_cursor(0)
    local parts = vim.split(text, '\n', { plain = true })
    vim.api.nvim_buf_set_text(0, cur[1] - 1, cur[2], cur[1] - 1, cur[2], parts)
    local row = cur[1] + #parts - 1
    local col = (#parts == 1) and cur[2] + #parts[#parts] or #parts[#parts]
    vim.api.nvim_win_set_cursor(0, { row, col })
  ]], text)
  settle()
end

local function run(seq)
  for _, item in ipairs(seq) do
    if type(item) == 'table' then vs(item.vs) else keys(item) end
  end
end

local function lines() return req('nvim_buf_get_lines', 0, 0, -1, false) end
local function reg(r) return lua('return vim.fn.getreg(...)', r) end
local function scene(new_lines, row, col)
  req('nvim_buf_set_lines', 0, 0, -1, false, new_lines)
  req('nvim_win_set_cursor', 0, { row or 1, col or 0 })
end

-- 1. cw + typed text: recorded and replayed.
scene({ 'one two three', 'four five six' })
run({ 'qa', 'cw', { vs = 'NEW' }, '<Esc>', 'q' })
eq(lines()[1], 'NEW two three', 'recording itself changes the word')
eq(reg('a'), 'cw' .. '\18\15="NEW"\r' .. '\27', 'register carries the text')
req('nvim_win_set_cursor', 0, { 2, 0 })
run({ '@a' })
eq(lines()[2], 'NEW five six', '@a replays the change with its text')

-- 2. Motions around the inserts survive; several sessions in one macro.
scene({ 'a b', 'c d' })
run({ 'qb', 'A', { vs = ';' }, '<Esc>', 'j', 'I', { vs = '> ' }, '<Esc>', 'q' })
eq(lines()[1], 'a b;', 'first session')
eq(lines()[2], '> c d', 'second session')
scene({ 'x y', 'z w' })
run({ '@b' })
eq(lines()[1], 'x y;', 'replay: append')
eq(lines()[2], '> z w', 'replay: motion then insert')

-- 3. Multi-line text with indentation: no second indent on replay.
lua('vim.bo.autoindent = true')
scene({ 'if x then', 'end' })
run({ 'qc', 'o', { vs = '  print(1)\n  print(2)' }, '<Esc>', 'q' })
eq(lines()[2], '  print(1)', 'recorded line 1')
eq(lines()[3], '  print(2)', 'recorded line 2')
scene({ 'if y then', 'end' })
run({ '@c' })
eq(lines()[2], '  print(1)', 'replayed line 1 not re-indented')
eq(lines()[3], '  print(2)', 'replayed line 2 not re-indented')
eq(lines()[4], 'end', 'rest of the buffer intact')
lua('vim.bo.autoindent = false')

-- 4. Text that would break a register or an expression.
local awkward = [[ "q" \n é€ |x	tab]]
scene({ 'k' })
run({ 'qd', 'A', { vs = awkward }, '<Esc>', 'q' })
eq(lines()[1], 'k' .. awkward, 'recorded awkward text')
local d = reg('d')
for i = 1, #d do
  if d:byte(i) >= 128 then fail('register must be pure ASCII (byte ' .. d:byte(i) .. ' at ' .. i .. ')') end
end
scene({ 'k' })
run({ '@d' })
eq(lines()[1], 'k' .. awkward, 'awkward text replays byte for byte')

-- 5. Empty insert: nothing spliced.
scene({ 'aa bb' })
run({ 'qe', 'cw', '<Esc>', 'q' })
eq(reg('e'), 'cw\27', 'empty insert keeps the plain keys')

-- 6. Uppercase register appends: only the new part is rewritten.
scene({ 'p q r' })
run({ 'qf', 'w', 'q' })
run({ 'qF', 'cw', { vs = 'Z' }, '<Esc>', 'q' })
eq(reg('f'), 'w' .. 'cw\18\15="Z"\r\27', 'append keeps the earlier part')
scene({ 'p q r' })
run({ '@f' })
eq(lines()[1], 'p Z r', 'appended macro replays')

-- 7. No insert: left exactly as nvim recorded it.
scene({ 'a b c' })
run({ 'qg', 'dw', 'q' })
eq(reg('g'), 'dw', 'no insert, no rewrite')

-- 8. Keys typed through nvim_input inside insert (the extension routes typed
-- characters that way while a remote edit is pending) are part of the
-- slice, not doubled.
scene({ 'm n' })
run({ 'qh', 'A', 'X', { vs = 'Y' }, '<Esc>', 'q' })
eq(lines()[1], 'm nXY', 'mixed typing recorded')
eq(reg('h'), 'A\18\15="XY"\r\27', 'routed keys replaced by the slice, not doubled')
scene({ 'm n' })
run({ '@h' })
eq(lines()[1], 'm nXY', 'mixed typing replays once')

-- 9. A count on the replay.
scene({ 'a', 'b', 'c' })
run({ 'qi', 'A', { vs = '!' }, '<Esc>', 'j', 'q' })
run({ '2@i' })
eq(table.concat(lines(), ','), 'a!,b!,c!', 'counted replay')

vim.fn.jobstop(chan)
print('macro_insert_tests: ALL OK')
