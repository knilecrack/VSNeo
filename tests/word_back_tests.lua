-- vsneo.word_back_boundary(row, col): the byte column where i_CTRL-W would
-- stop, computed nvim-side while Visual Studio performs the deletion.
-- row is 1-based, col is a 0-based BYTE offset, and so is the result.

local t = dofile('tests/helper.lua')
t.setup({
  name = 'C:/test/words.lua',
  lines = {
    'hello world',        -- 1
    'hello wo',           -- 2
    'foo   ',             -- 3: trailing whitespace goes with the word
    'foo.bar',            -- 4: keyword word after punctuation
    'foo.',               -- 5: trailing punctuation run
    'foo. bar',           -- 6: class switch across a space
    '',                   -- 7: empty line
    'nonempty',           -- 8
    'héllo wörld',        -- 9: multi-byte; é and ö are 2 bytes each
  },
})

-- plain word from end of line
t.eq(vsneo.word_back_boundary(1, 11), 6, 'hello world| stops at the space')
-- from mid-word
t.eq(vsneo.word_back_boundary(2, 8), 6, 'hello wo| stops at the space')
-- trailing whitespace is deleted together with the word before it
t.eq(vsneo.word_back_boundary(3, 6), 0, 'foo   | deletes back to line start')
-- the caret is after a keyword char: only the word "bar" is deleted
t.eq(vsneo.word_back_boundary(4, 7), 4, 'foo.bar| deletes bar, stops after the dot')
-- trailing punctuation is its own class
t.eq(vsneo.word_back_boundary(5, 4), 3, 'foo.| deletes the dot')
-- keyword after punctuation and a space
t.eq(vsneo.word_back_boundary(6, 8), 5, 'foo. bar| stops after the space')
-- empty line and column 0 are both a no-op
t.eq(vsneo.word_back_boundary(7, 0), 0, 'empty line stays at 0')
t.eq(vsneo.word_back_boundary(8, 0), 0, 'column 0 stays at 0')
-- multi-byte: the answer is a BYTE column. "héllo " is 7 bytes (é is 2).
t.eq(vsneo.word_back_boundary(9, 13), 7, 'héllo wörld| stops after the space (byte 7)')

print('word_back_tests: ALL OK')
