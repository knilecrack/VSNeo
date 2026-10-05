-- What '.' and macros capture as "the inserted text" must not depend on
-- where nvim's cursor happens to be. A bracket opened at insert entry
-- follows the text through every buffer change; these cases are the ones a
-- cursor-slice capture got wrong (issue #37): insert left through a mapping that moves on, a
-- caret push ahead of its edit, no caret push at all.
--
-- Driven like production: a child nvim over RPC. Keys go in with nvim_input;
-- Visual Studio typing is vsneo.apply_spans (the mirror's write path), and
-- the caret is pushed with vsneo.set_cursor - or deliberately not.

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

local function settle() lua('vim.wait(40)') end
local function keys(k) req('nvim_input', k); settle() end
local function lines() return req('nvim_buf_get_lines', 0, 0, -1, false) end
local function all() return table.concat(lines(), '|') end
local function cursor() return req('nvim_win_get_cursor', 0) end
local function buf() return req('nvim_get_current_buf') end
local function scene(new_lines, row, col)
  req('nvim_buf_set_lines', 0, 0, -1, false, new_lines)
  req('nvim_win_set_cursor', 0, { row or 1, col or 0 })
  settle()
end
local function span(r1, c1, r2, c2, text)
  lua('return vsneo.apply_spans(...)', buf(), { { r1, c1, r2, c2, vim.split(text, '\n', { plain = true }) } })
end

-- Visual Studio typing, one character per write, starting at nvim's cursor.
-- caret: 'after' pushes the caret behind each edit, 'before' ahead of it (a
-- push that won the race), 'none' never. Returns where the caret ended up
-- (1-based row, 0-based byte column).
local function vs_type(text, caret)
  local cur = cursor()
  local row, col = cur[1], cur[2]
  for i = 1, #text do
    if caret == 'before' then pcall(lua, 'vsneo.set_cursor(...)', row, col + 1) end
    span(row - 1, col, row - 1, col, text:sub(i, i))
    col = col + 1
    if caret == 'after' then lua('vsneo.set_cursor(...)', row, col) end
  end
  return row, col
end

local SRC = {
  'bool Handle_Introspect (mg_connection* pConnection) const;',
  'bool Handle_LinkResolve (mg_connection* pConnection) const;',
  'bool Handle_LoginPage (mg_connection* pConnection) const;',
}

-- 1. The cursor's part in the capture, removed. Whatever the caret pushes
-- did, the three changes from the report repeat exactly.
for _, caret in ipairs({ 'after', 'before', 'none' }) do
  scene(SRC, 1, 12)
  keys('cw'); vs_type('XYZ', caret); keys('<Esc>')
  eq(lines()[1], 'bool Handle_XYZ (mg_connection* pConnection) const;', 'cw applied, caret ' .. caret)
  req('nvim_win_set_cursor', 0, { 2, 12 })
  keys('.')
  eq(lines()[2], 'bool Handle_XYZ (mg_connection* pConnection) const;', '. repeats cw, caret ' .. caret)

  scene(SRC, 1, 5)
  keys('ct_'); vs_type('XYZ', caret); keys('<Esc>')
  req('nvim_win_set_cursor', 0, { 2, 5 })
  keys('.')
  eq(lines()[2], 'bool XYZ_LinkResolve (mg_connection* pConnection) const;', '. repeats ct_, caret ' .. caret)

  scene(SRC, 1, 5)
  keys('c3l'); vs_type('XYZ', caret); keys('<Esc>')
  req('nvim_win_set_cursor', 0, { 2, 5 })
  keys('.')
  eq(lines()[2], 'bool XYZdle_LinkResolve (mg_connection* pConnection) const;', '. repeats c3l, caret ' .. caret)
end

-- 2. The caret pushed only once the Escape is already on its way.
scene(SRC, 1, 12)
keys('cw')
local row, col = vs_type('XYZ', 'none')
req('nvim_input', '<Esc>')
lua('vsneo.set_cursor(...)', row, col)
settle()
req('nvim_win_set_cursor', 0, { 2, 12 })
keys('.')
eq(lines()[2], 'bool Handle_XYZ (mg_connection* pConnection) const;', '. repeats cw, caret pushed after Escape')

-- 3. Insert left through a mapping that keeps moving. The capture used to
-- run after the whole rhs, with the cursor wherever the mapping left it:
-- <Esc><Right> claimed the character after the text, <Esc><Down><Right> a
-- line and a half.
req('nvim_command', 'imap <Left> <Esc>')
req('nvim_command', 'imap <Right> <Esc><Right>')
req('nvim_command', 'imap <Down> <Esc><Down><Right>')
for _, exit in ipairs({ '<Left>', '<Right>', '<Down>' }) do
  scene(SRC, 1, 12)
  keys('cw')
  row, col = vs_type('XYZ', 'none')
  lua('vsneo.set_cursor(...)', row, col)  -- the forced caret sync ahead of the mapped key
  keys(exit)
  req('nvim_win_set_cursor', 0, { 2, 12 })
  keys('.')
  eq(all(), table.concat({
    'bool Handle_XYZ (mg_connection* pConnection) const;',
    'bool Handle_XYZ (mg_connection* pConnection) const;',
    SRC[3] }, '|'), '. after leaving insert with ' .. exit)
end
req('nvim_command', 'iunmap <Left>')
req('nvim_command', 'iunmap <Right>')
req('nvim_command', 'iunmap <Down>')

-- 4. A completion commit replaces the typed prefix: the result is the text.
scene({ 'x = foo;', 'y = foo;' }, 1, 4)
keys('cw')
vs_type('Con', 'after')
span(0, 4, 0, 7, 'Console')
keys('<Esc>')
eq(lines()[1], 'x = Console;', 'completion committed')
req('nvim_win_set_cursor', 0, { 2, 4 })
keys('.')
eq(lines()[2], 'y = Console;', '. repeats the committed text')

-- 5. Brace completion: the closer lands behind the caret and is part of
-- what the session inserted.
scene({ 'a = old;', 'b = old;' }, 1, 4)
keys('cw')
vs_type('f(', 'after')
span(0, 6, 0, 6, ')')
keys('<Esc>')
req('nvim_win_set_cursor', 0, { 2, 4 })
keys('.')
eq(lines()[2], 'b = f();', '. repeats text and auto-inserted closer')

-- 6. Edits elsewhere during the session (a using directive added above, the
-- indent reformatted in front) move the bracket, they do not widen it.
scene({ '    one = 1;', '    two = 2;' }, 1, 4)
keys('cw')
vs_type('uno', 'after')
span(0, 0, 0, 0, 'using X;\n')
span(1, 0, 1, 4, '\t')
keys('<Esc>')
eq(all(), 'using X;|\tuno = 1;|    two = 2;', 'edits around the insert applied')
req('nvim_win_set_cursor', 0, { 3, 4 })
keys('.')
eq(lines()[3], '    uno = 2;', '. repeats only what was typed')

-- 7. Backspacing over text that was there before, then typing: the typed
-- text is captured (the old slice saw an empty insert and '.' only deleted).
scene({ 'ab cd ef', 'ab cd ef' }, 1, 3)
keys('cw')
span(0, 1, 0, 3, '')
span(0, 1, 0, 1, 'Q')
span(0, 2, 0, 2, 'R')
keys('<Esc>')
eq(lines()[1], 'aQR ef', 'backspace then typing applied')
req('nvim_win_set_cursor', 0, { 2, 3 })
keys('.')
eq(lines()[2], 'ab QR ef', '. repeats the typed text')

-- 8. More than a handful of lines is still a change (the old 5-line guard
-- existed only because the slice could not be trusted).
scene({ 'top', 'end' }, 1, 0)
keys('o')
span(1, 0, 1, 0, 'l1\nl2\nl3\nl4\nl5\nl6\nl7')
keys('<Esc>')
eq(#lines(), 9, 'seven lines typed')
req('nvim_win_set_cursor', 0, { 9, 0 })
keys('.')
eq(all(), 'top|l1|l2|l3|l4|l5|l6|l7|end|l1|l2|l3|l4|l5|l6|l7', '. repeats a seven-line insert')

-- 9. o then Escape with autoindent: nvim takes the indent back, nothing
-- was inserted, and '.' opens another empty line.
lua('vim.bo.autoindent = true')
scene({ '    body', 'tail' }, 1, 4)
keys('o'); keys('<Esc>')
req('nvim_win_set_cursor', 0, { 3, 0 })
keys('.')
eq(all(), '    body||tail|', '. repeats an empty o')
lua('vim.bo.autoindent = false')

-- 10. The line rewritten around the insert (a mirror resend): nothing is
-- left to say what was typed. The change is refused - '.' must not fall back to native redo, which holds
-- the keys with an empty insertion and would delete the next target.
scene({ 'one two', 'six ten' }, 1, 0)
keys('cw')
vs_type('uno', 'after')
req('nvim_buf_set_lines', 0, 0, 1, false, { 'uno two' })
keys('<Esc>')
req('nvim_win_set_cursor', 0, { 2, 0 })
keys('.')
eq(all(), 'uno two|six ten', '. refuses a change whose text was not captured')
-- A later change that never enters insert takes redo back.
keys('x')
keys('.')
eq(lines()[2], 'x ten', 'native . owns redo again after another change')

-- 11. Same rewrite through set_text, and one that only crosses the end.
scene({ 'one two', 'six ten' }, 1, 0)
keys('cw')
vs_type('uno', 'after')
span(0, 0, 0, 7, 'uno two')
keys('<Esc>')
req('nvim_win_set_cursor', 0, { 2, 0 })
keys('.')
eq(all(), 'uno two|six ten', 'whole-line set_text taints the capture')

scene({ 'one two', 'six ten' }, 1, 0)
keys('cw')
vs_type('uno', 'after')
span(0, 1, 0, 5, 'XX')
keys('<Esc>')
eq(lines()[1], 'uXXwo', 'replacement across the end of the typed text applied')
req('nvim_win_set_cursor', 0, { 2, 0 })
keys('.')
eq(lines()[2], 'six ten', 'a replacement crossing the bracket taints the capture')

-- 12. Macros read the same bracket: no caret push, the text is recorded.
scene({ 'one two three', 'four five six' }, 1, 0)
keys('qa'); keys('cw'); vs_type('NEW', 'none'); keys('<Esc>'); keys('q')
eq(lua('return vim.fn.getreg("a")'), 'cw' .. '\18\15="NEW"\r' .. '\27', 'register carries the text without a caret push')
req('nvim_win_set_cursor', 0, { 2, 0 })
keys('@a')
eq(lines()[2], 'NEW five six', '@a replays the change with its text')

-- 13. An insert mapping that edits after leaving: the change is on record
-- before the rest of the rhs runs, so the dd that follows moves the tick
-- past it and takes redo - '.' repeats the dd, not the insert.
req('nvim_command', 'imap <F5> <Esc>dd')
scene({ 'one two', 'l2', 'l3', 'l4' }, 1, 0)
keys('cw'); vs_type('uno', 'after'); keys('<F5>')
eq(all(), 'l2|l3|l4', 'mapping left insert and deleted the line')
keys('.')
eq(all(), 'l3|l4', '. repeats the dd the mapping ended with')
req('nvim_command', 'iunmap <F5>')

-- 14. u is Visual Studio's undo: it never reaches nvim as a key, and its
-- text change comes back through the mirror like any other VS edit. That is
-- not a Vim change and must not take redo away - change, u, '.' used to
-- fall back to native redo and delete the next word, inserting nothing.
local function changed_line1()
  scene({ 'one two three', 'four five six', 'seven eight nine' }, 1, 4)
  keys('cw'); vs_type('XYZ', 'after'); keys('<Esc>')
end
changed_line1()
span(0, 4, 0, 7, '')      -- u: the typing
span(0, 4, 0, 4, 'two')   -- u: the deletion
eq(lines()[1], 'one two three', 'undo restored the line')
req('nvim_win_set_cursor', 0, { 2, 5 })
keys('.')
eq(lines()[2], 'four XYZ six', '. repeats the change after it was undone')

-- ... and the same after undoing a '.' itself.
changed_line1()
req('nvim_win_set_cursor', 0, { 2, 5 })
keys('.')
span(1, 5, 1, 8, 'five')  -- u: the dot's result
req('nvim_win_set_cursor', 0, { 3, 6 })
keys('.')
eq(lines()[3], 'seven XYZ nine', '. repeats again after its own result was undone')

-- ... and after an unrelated Visual Studio edit (a format, a refactoring),
-- by spans or by a whole-buffer resend.
changed_line1()
span(2, 0, 2, 0, '  ')
req('nvim_win_set_cursor', 0, { 2, 5 })
keys('.')
eq(lines()[2], 'four XYZ six', '. survives an unrelated Visual Studio edit')
lua('return vsneo.set_all_lines(...)', buf(), { 'one XYZ three', 'four XYZ six', 'seven eight nine' })
req('nvim_win_set_cursor', 0, { 3, 6 })
keys('.')
eq(lines()[3], 'seven XYZ nine', '. survives a whole-buffer resend')

-- A change nvim made itself still takes redo, and keeps it across an undo.
changed_line1()
req('nvim_win_set_cursor', 0, { 2, 0 })
keys('x')
span(1, 0, 1, 0, 'f')     -- u: the x
req('nvim_win_set_cursor', 0, { 2, 0 })  -- the caret push that follows the undo
keys('.')
eq(lines()[2], 'our five six', 'native . repeats the x, not the earlier insert-change')

vim.fn.jobstop(chan)
print('dot_capture_tests: ALL OK')
