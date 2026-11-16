local function user_mapped(lhs, mode)
  local now = vim.fn.maparg(lhs, mode, false, true)
  return next(now) ~= nil and not vim.deep_equal(now, maps_before_rc[mode .. lhs])
end

-- pcall: a broken rc must not abort the companion, or the re-assert below -
-- and with it the whole viewport contract - would silently not happen.
local rc = vim.fn.expand('~/.vsneorc')
if vim.fn.filereadable(rc) == 1 then
  local ok, err = pcall(vim.cmd, 'source ' .. vim.fn.fnameescape(rc))
  if not ok then
    -- Nothing renders vim.notify here; the cmdline margin does render
    -- ext_messages, and :messages keeps it for later.
    vim.notify('VSNeo: ~/.vsneorc failed: ' .. tostring(err), vim.log.levels.ERROR)
  end
end

-- The Lua twin, sourced after the vimscript rc. vimscript mappings cannot
-- carry a desc, and the which-key popup reads desc first - so this is where
-- mappings meant to show up with real names live (see examples/vsneorc.lua).
local luarc = vim.fn.expand('~/.vsneorc.lua')
if vim.fn.filereadable(luarc) == 1 then
  local ok, err = pcall(dofile, luarc)
  if not ok then
    vim.notify('VSNeo: ~/.vsneorc.lua failed: ' .. tostring(err), vim.log.levels.ERROR)
  end
end

-- The <leader> defaults, after the rc so that its mapleader applies (both
-- sample rcs set it to Space, and got \rn instead). A user mapping on the
-- same keys wins: only an unmapped lhs gets the default.
for lhs, command in pairs({
  ['<leader>rn'] = 'Refactor.Rename',
  ['<leader>ca'] = 'View.QuickActionsForPosition',
  ['<leader>f'] = 'Edit.FormatDocument',
  -- nvim's undo tree must not run (see :Undo above); these walk Visual
  -- Studio's history instead. U has no line-undo equivalent: plain undo.
  ['g-'] = 'Edit.Undo',
  ['g+'] = 'Edit.Redo',
  ['U'] = 'Edit.Undo',
}) do
  if vim.fn.maparg(lhs, 'n') == '' then act(lhs, command) end
end

-- The Seeky pickers under <leader>s, Telescope-style. Stricter than the rule
-- above: a user mapping that overlaps the keys at all wins (mapcheck), since
-- a default <leader>sw beside a user's <leader>sw) would make the user's wait
-- out 'timeoutlen'. The Ctrl+Shift+Alt chords stay as well - they work from
-- insert mode, these are for normal mode.
for _, m in ipairs({
  { '<leader>sf', 'files', 'files' },
  { '<leader>sg', 'grep', 'live grep' },
  { '<leader>sw', 'grep', 'grep the word under the cursor', true },
  { '<leader>s/', 'lines', 'search the current file' },
  { '<leader>ss', 'symbols', 'workspace symbols' },
  { '<leader>so', 'outline', 'document outline' },
  { '<leader>sm', 'git', 'git modified files' },
  { '<leader>sr', 'resume', 'resume the last picker' },
  { '<leader>sb', 'buffers', 'open documents' },
  { '<leader>s.', 'oldfiles', 'recent files' },
  { "<leader>s'", 'marks', 'marks' },
  { '<leader>s"', 'registers', 'registers' },
  { '<leader>sd', 'diagnostics', 'diagnostics (Error List)' },
  { '<leader>sk', 'keymaps', 'keymaps' },
  { '<leader>s:', 'command_history', 'command history' },
  { '<leader>s?', 'search_history', 'search history' },
}) do
  local lhs, mode, what, word = m[1], m[2], m[3], m[4]
  if vim.fn.mapcheck(lhs, 'n') == '' then
    vim.keymap.set('n', lhs, function()
      _G.vsneo.seeky(mode, word and vim.fn.expand('<cword>') or '')
    end, { silent = true, desc = 'Seeky: ' .. what })
  end
end

-- Semantic selection in visual mode: + grows to the enclosing syntax node,
-- - shrinks back (nvim-treesitter's incremental selection, by the language
-- service). Visual-mode + and - are the line motions nobody uses there.
for lhs, command in pairs({
  ['+'] = 'Edit.ExpandSelection',
  ['-'] = 'Edit.ContractSelection',
}) do
  if vim.fn.maparg(lhs, 'x') == '' then
    vim.keymap.set('x', lhs, function() _G.vsneo.cmd_select(command) end,
      { desc = (lhs == '+' and 'Expand' or 'Contract') .. ' selection (VS)' })
  end
end

-- Visual Studio commands as operators (vsneo.operator), unless the rc
-- mapped the keys. gc is comment.nvim's key; = replaces nvim's own, whose
-- 'indentexpr' is not the language service's formatter and gets C# wrong.
for _, op in ipairs({
  { 'gc', 'Edit.CommentSelection', 'Comment (VS)' },
  { '=', 'Edit.FormatSelection', 'Format (VS)' },
}) do
  local lhs, command, desc = op[1], op[2], op[3]
  local line_lhs = lhs .. lhs:sub(-1)
  if not user_mapped(lhs, 'n') and not user_mapped(lhs, 'x') then
    vim.keymap.set({ 'n', 'x' }, lhs, _G.vsneo.operator(command), { expr = true, desc = desc })
  end
  -- gcc / == : the current line, like comment.nvim and Vim's ==.
  if not user_mapped(line_lhs, 'n') then
    vim.keymap.set('n', line_lhs, function() return _G.vsneo.operator(command)() .. '_' end,
      { expr = true, desc = desc .. ' line' })
  end
end

------------------------------------------------------------------
-- Syntax text objects and motions
--
-- af/if (function), ac/ic (class), ]m [m ]M [M (method start/end), ]] [[
-- (type start). The best answer available, in order:
--   1. Visual Studio: Roslyn for C# (Editor/SyntaxTargets.cs), its code model
--      for C++ and anything else that has one (Editor/CodeModelTargets.cs).
--      nvim asks
--      with an rpcrequest and waits: an operator-pending or motion key, so
--      the round trip costs nothing anyone feels, and the extension always
--      answers (a timeout answers too). "no_tree" means Roslyn does not own
--      this file; nil means it does and there is no target.
--   2. nvim's treesitter, when nvim has a parser for the file's language
--      (it ships C; C++ and others need one installed). Only the parser is
--      used - VSNeo keeps treesitter's highlighter off.
--   3. Visual Studio's outlining regions, already mirrored in as folds: the
--      innermost region around the cursor for af/if and ac/ic alike (regions
--      do not say what they are), a count going outward.
--   4. For the motions, nvim's built-in key: ]] [[ ]m [m mean something in C
--      and C++ code, and must not turn into nothing there.
--
-- % across #if/#elif/#else/#endif and #region/#endregion needs none of
-- this: nvim's own matchit does it, from the C# and C ftplugins.
------------------------------------------------------------------

local NO_TREE = 'no_tree'

local function vs_syntax(op, count)
  local buf, tick = vim.api.nvim_get_current_buf(), vim.b.changedtick
  local cur = vim.api.nvim_win_get_cursor(0)
  local ok, res = pcall(vim.rpcrequest, chan, 'vsneo_syntax',
    vim.api.nvim_buf_get_name(0), cur[1], cur[2], op, count or 1)
  if not ok then
    -- A failed or timed-out answer is no target, not "no tree": the
    -- fallbacks would answer for a file Visual Studio does own - a C# dac
    -- took the innermost outlining region (the method) for the class.
    vim.api.nvim_echo({ { 'VSNeo: ' .. tostring(res), 'WarningMsg' } }, false, {})
    return nil
  end
  -- nvim services other calls while it waits (a tab switch, a caret or
  -- scroll echo), and an answer about a buffer, text or cursor that has
  -- moved since describes something else: daf deleted rows of whichever
  -- buffer was current by then. Refusing is not enough under an operator:
  -- the cursor's move would be the motion it applies to, so it goes back.
  local now = vim.api.nvim_win_get_cursor(0)
  if vim.api.nvim_get_current_buf() ~= buf then return nil end
  if vim.b.changedtick ~= tick or now[1] ~= cur[1] or now[2] ~= cur[2] then
    pcall(vim.api.nvim_win_set_cursor, 0, cur)
    return nil
  end
  if res == nil or res == vim.NIL then return nil end
  return res
end

-- Linewise when the range owns its lines (starts at the first non-blank,
-- ends at the last), as SyntaxTargets decides for Roslyn. Rows 1-based,
-- columns 0-based bytes, end inclusive - the shape every source returns.
local function owns_lines(sr, sc, er, ec)
  local first = vim.api.nvim_buf_get_lines(0, sr - 1, sr, false)[1] or ''
  local last = vim.api.nvim_buf_get_lines(0, er - 1, er, false)[1] or ''
  return first:sub(1, sc):match('^%s*$') ~= nil and last:sub(ec + 2):match('^%s*$') ~= nil
end

local function range_target(sr, sc, er, ec)
  return { sr, sc, er, ec, owns_lines(sr, sc, er, ec) }
end

-- Between a pair of braces (1-based rows, 0-based byte columns of { and }):
-- the lines strictly inside when each brace stands alone at its end of the
-- body, else the characters between them, whitespace trimmed; nil when
-- nothing is between. The rule SyntaxTargets.BetweenBraces keeps: line
-- distance alone made `{ a = 1;` over `return a; }` empty, and with a line
-- between them dif deleted only that line.
local function between_braces(open_row, open_col, close_row, close_col)
  local function line(r) return vim.api.nvim_buf_get_lines(0, r - 1, r, false)[1] or '' end
  if close_row > open_row
     and line(open_row):sub(open_col + 2):match('^%s*$')
     and line(close_row):sub(1, close_col):match('^%s*$') then
    if close_row - open_row == 1 then return nil end
    return { open_row + 1, 0, close_row - 1, math.max(#line(close_row - 1) - 1, 0), true }
  end
  local parts = vim.api.nvim_buf_get_text(0, open_row - 1, open_col + 1, close_row - 1, close_col, {})
  local si, sc, ei, ec
  for i, p in ipairs(parts) do
    local c = p:find('%S')
    if c then si, sc = i, c - 1; break end
  end
  if not si then return nil end
  for i = #parts, 1, -1 do
    local c = parts[i]:match('.*()%S')
    if c then ei, ec = i, c - 1; break end
  end
  -- The first part starts after the {; the others at their line's start.
  local function col(i, c) return i == 1 and open_col + 1 + c or c end
  return { open_row + si - 1, col(si, sc), open_row + ei - 1, col(ei, ec), false }
end

-- ---- 2. treesitter ------------------------------------------------------

-- Node kinds across the common grammars (C, C++, C#, Java, JS/TS, Rust, Go,
-- Python, Lua). Declarations without a body are not functions here.
local TS_FUNCTION = {
  function_definition = true, function_declaration = true, method_definition = true,
  method_declaration = true, constructor_declaration = true, destructor_declaration = true,
  lambda_expression = true, arrow_function = true, ['function'] = true, function_expression = true,
  function_item = true, local_function_statement = true, func_literal = true,
}
local TS_MEMBER = {   -- for ]m: not lambdas
  function_definition = true, function_declaration = true, method_definition = true,
  method_declaration = true, constructor_declaration = true, destructor_declaration = true,
  function_item = true, local_function_statement = true,
}
local TS_CLASS = {
  class_specifier = true, struct_specifier = true, union_specifier = true, enum_specifier = true,
  class_declaration = true, struct_declaration = true, interface_declaration = true,
  record_declaration = true, enum_declaration = true, class_definition = true, class = true,
  struct_item = true, enum_item = true, impl_item = true, trait_item = true,
}

local function ts_root()
  local ok, parser = pcall(vim.treesitter.get_parser, 0)
  if not ok or not parser then return nil end
  local ok2, trees = pcall(function() return parser:parse() end)
  if not ok2 or not trees or not trees[1] then return nil end
  return trees[1]:root()
end

local function ts_body(node)
  local body = node:field('body')[1]
  if body then return body end
  for child in node:iter_children() do   -- C specifiers: field_declaration_list
    local t = child:type()
    if t == 'field_declaration_list' or t == 'declaration_list' or t == 'class_body'
       or t == 'compound_statement' or t == 'block' or t == 'enumerator_list' then
      return child
    end
  end
  return nil
end

local function ts_matches(node, kinds)
  return kinds[node:type()] and (kinds ~= TS_CLASS or ts_body(node) ~= nil)
end

-- nil only when there is no parser: a tree with nothing around the cursor is
-- false, an answer - not a reason to fall back to the outlining regions (daf
-- in a C #include block took the region of the includes).
local function ts_object(op, count)
  local root = ts_root()
  if not root then return nil end
  local kinds = op:match('^function') and TS_FUNCTION or TS_CLASS
  local cur = vim.api.nvim_win_get_cursor(0)
  local line = vim.api.nvim_get_current_line()
  -- Indentation counts as inside the declaration that starts on the line.
  local col = math.max(cur[2], (line:find('%S') or 1) - 1)
  local node = root:named_descendant_for_range(cur[1] - 1, col, cur[1] - 1, col)
  local depth = 0
  while node do
    if ts_matches(node, kinds) then
      depth = depth + 1
      if depth == count then break end
    end
    node = node:parent()
  end
  if not node then return false end

  if op:match('_outer$') then
    local sr, sc, er, ec = node:range()   -- 0-based, end exclusive
    if ec == 0 and er > sr then er = er - 1; ec = #(vim.api.nvim_buf_get_lines(0, er, er + 1, false)[1] or '') end
    return range_target(sr + 1, sc, er + 1, math.max(ec - 1, 0))
  end
  local body = ts_body(node)
  if not body then return false end
  local br, bc, er, ec = body:range()
  local text = vim.treesitter.get_node_text(body, 0)
  if text:sub(1, 1) == '{' and text:sub(-1) == '}' then
    return between_braces(br + 1, bc, er + 1, ec - 1) or false
  end
  return { br + 1, bc, er + 1, math.max(ec - 1, 0), false }   -- an expression body
end

local function ts_motion(op, count)
  local root = ts_root()
  if not root then return nil end
  local kinds = op:match('^function') and TS_MEMBER or TS_CLASS
  local want_end = op:match('_end$') ~= nil
  local positions = {}
  local function walk(node)
    if ts_matches(node, kinds) then
      local sr, sc, er, ec = node:range()
      if want_end then
        if ec == 0 and er > sr then er = er - 1; ec = #(vim.api.nvim_buf_get_lines(0, er, er + 1, false)[1] or '') end
        positions[#positions + 1] = { er + 1, math.max(ec - 1, 0) }
      else
        positions[#positions + 1] = { sr + 1, sc }
      end
    end
    for child in node:iter_children() do
      if child:named() then walk(child) end
    end
  end
  walk(root)
  table.sort(positions, function(a, b) return a[1] < b[1] or (a[1] == b[1] and a[2] < b[2]) end)
  local cur = vim.api.nvim_win_get_cursor(0)
  local function after(p) return p[1] > cur[1] or (p[1] == cur[1] and p[2] > cur[2]) end
  local function before(p) return p[1] < cur[1] or (p[1] == cur[1] and p[2] < cur[2]) end
  local seen = 0
  if op:match('_next_') then
    for _, p in ipairs(positions) do
      if after(p) then seen = seen + 1; if seen == count then return { p[1], p[2], p[1], p[2], false } end end
    end
  else
    for i = #positions, 1, -1 do
      if before(positions[i]) then seen = seen + 1; if seen == count then return { positions[i][1], positions[i][2], positions[i][1], positions[i][2], false } end end
    end
  end
  return false   -- a tree, and nothing further: not a reason to fall back
end

-- ---- 3. Visual Studio's outlining regions ------------------------------

-- The same regions ar/ir select (region_around); only the shape differs.
local function region_object(op, count)
  local r = region_around(vim.api.nvim_win_get_cursor(0)[1], count)
  if not r then return nil end
  local s, e = r.s, r.e
  local last = vim.api.nvim_buf_get_lines(0, e - 1, e, false)[1] or ''
  if op:match('_outer$') then return { s, 0, e, math.max(#last - 1, 0), true } end
  -- Inner: between the header and the closing line, skipping a lone '{'.
  local is = s + 1
  if (vim.api.nvim_buf_get_lines(0, is - 1, is, false)[1] or ''):match('^%s*{%s*$') then is = is + 1 end
  local ie = e - 1
  if ie < is then return nil end
  local ilast = vim.api.nvim_buf_get_lines(0, ie - 1, ie, false)[1] or ''
  return { is, 0, ie, math.max(#ilast - 1, 0), true }
end

-- ---- the mappings --------------------------------------------------------

-- [startRow, startByte, endRow, endByteInclusive, linewise] as a selection.
-- In operator-pending mode the visual selection is what the operator acts on
-- (the text-object technique); in visual mode the kind switches if needed.
local function select_syntax_target(t)
  local want = t[5] and 'V' or 'v'
  if vim.api.nvim_get_mode().mode ~= want then vim.cmd('normal! ' .. want) end
  vim.api.nvim_win_set_cursor(0, { t[1], t[2] })
  vim.cmd('normal! o')
  vim.api.nvim_win_set_cursor(0, { t[3], t[4] })
end

for _, obj in ipairs({
  { 'af', 'function_outer', 'a function' },
  { 'if', 'function_inner', 'inner function' },
  { 'ac', 'class_outer', 'a class' },
  { 'ic', 'class_inner', 'inner class' },
}) do
  local lhs, op, desc = obj[1], obj[2], obj[3]
  if vim.fn.maparg(lhs, 'x') == '' and vim.fn.maparg(lhs, 'o') == '' then
    vim.keymap.set({ 'x', 'o' }, lhs, function()
      local count = vim.v.count1
      local t = vs_syntax(op, count)
      if t == NO_TREE then
        t = ts_object(op, count)
        if t == nil then t = region_object(op, count) end
      end
      if type(t) == 'table' then select_syntax_target(t) end
    end, { desc = desc })
  end
end

-- Motions: a jumplist entry so '' comes back, and linewise under an operator.
-- A start motion is exclusive there, as Vim's ]m is: d]m takes whole lines up
-- to the line before the next method, d[m from the method's line to the one
-- before the cursor - inclusive, it deleted the next declaration's header and
-- left its { behind. An end motion (]M [M) includes its line. With no tree
-- anywhere, the built-in key runs instead, with the same count - fed
-- noremap, so it is nvim's own, and under an operator it completes it.
for _, m in ipairs({
  { ']m', 'function_next_start', 'next method start' },
  { '[m', 'function_prev_start', 'previous method start' },
  { ']M', 'function_next_end', 'next method end' },
  { '[M', 'function_prev_end', 'previous method end' },
  { ']]', 'class_next_start', 'next type' },
  { '[[', 'class_prev_start', 'previous type' },
}) do
  local lhs, op, desc = m[1], m[2], m[3]
  if vim.fn.maparg(lhs, 'n') == '' and vim.fn.maparg(lhs, 'x') == '' and vim.fn.maparg(lhs, 'o') == '' then
    vim.keymap.set({ 'n', 'x', 'o' }, lhs, function()
      local count = vim.v.count1
      local t = vs_syntax(op, count)
      if t == NO_TREE then
        t = ts_motion(op, count)
        if t == nil then
          local keys = (vim.v.count > 0 and tostring(vim.v.count) or '') .. lhs
          vim.api.nvim_feedkeys(keys, 'n', false)
          return
        end
      end
      if type(t) ~= 'table' then return end
      vim.cmd("normal! m'")
      if vim.api.nvim_get_mode().mode:sub(1, 2) == 'no' then
        if op:match('_start$') then
          local row = vim.api.nvim_win_get_cursor(0)[1]
          local a, b = row, t[1] - 1                     -- down: up to the line before
          if t[1] < row then a, b = t[1], row - 1 end    -- up: from it, not the cursor's
          -- On the cursor's own line the plain move is Vim's exclusive charwise one.
          if t[1] ~= row and b >= a then return select_syntax_target({ a, 0, b, 0, true }) end
        else
          vim.cmd('normal! V')
        end
      end
      vim.api.nvim_win_set_cursor(0, { t[1], t[2] })
    end, { desc = desc })
  end
end

-- These are invariants, not preferences (see the top of this file for why each
-- one matters): the viewport synchroniser, the mirrored buffer and the
-- invisible-chrome layout all assume them. A user rc runs after the initial
-- setup and could casually break any of them with a 'set wrap', so they are
-- asserted again, last, unconditionally - and again after every :source of
-- the rc, which used to bring 'set inccommand=nosplit' (the preview-edit
-- hazard above) or wrap straight back.
-- SourcePost fires for every ftplugin, indent and syntax file sourced on a
-- FileType - half a dozen per opened C# file - and each of the pushes that
-- ride it used to go out for all of them, fanned out as a UI post to every
-- open view. Nothing under $VIMRUNTIME changes a vsneo setting; a user's
-- own :source of any file still does. ColorScheme and doautocmd (no file)
-- pass. The keymap push stays broad on purpose: plugins define mappings.
local runtime_dir = vim.fs.normalize(vim.env.VIMRUNTIME or ''):lower()
