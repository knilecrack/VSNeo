-- vsneo_search_count: the [current/total] chip SearchHighlightAdornment draws
-- beside the match under the cursor. The count itself is computed in Visual
-- Studio from vsneo_search_matches; the companion only carries the switch, as
-- the optional fourth value of vsneo_highlights.

local t = dofile('tests/helper.lua')
local h = t.setup({ name = 'C:/test/search_count.lua' })

local r = t.report(h, 'vsneo_highlights')
t.expect(r ~= nil, 'highlights pushed at load')
t.eq(r[5], 1, 'the counter is on by default')

local function source(lines)
  local path = vim.fn.tempname() .. '.vim'
  vim.fn.writefile(lines, path)
  t.clear(h)
  vim.cmd('source ' .. vim.fn.fnameescape(path))
  return t.report(h, 'vsneo_highlights')
end

r = source({ 'let g:vsneo_search_count = 0' })
t.expect(r ~= nil, ':source re-sends the switch')
t.eq(r[5], 0, '0 turns it off')

r = source({ 'let g:vsneo_search_count = v:false' })
t.eq(r[5], 0, 'false turns it off')

r = source({ 'let g:vsneo_search_count = 1' })
t.eq(r[5], 1, '1 turns it back on')

vim.g.vsneo_search_count = nil
r = source({ '" re-push' })
t.eq(r[5], 1, 'unset is on')
t.eq(#r, 5, 'three colors and the switch')

print('search_count_tests: ok')
