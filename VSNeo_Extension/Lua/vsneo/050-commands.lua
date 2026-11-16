function _G.vsneo._set_vs_commands(names)
  vs_command_names = names
end

vim.api.nvim_create_user_command('Vsc', function(opts)
  _G.vsneo.cmd(opts.args)
end, {
  nargs = '+',
  desc = 'VSNeo: run a Visual Studio command',
  -- Any part of the name, ignoring case (joinl finds Edit.JoinLines), names
  -- that start with it first. The command name only: what follows it is the
  -- command's own argument. Anchored on Vsc, so :silent Vsc completes too.
  -- Read off the text before the cursor, not the lead: with the cursor in
  -- the name of :Vsc Edit.F file.cs, nvim passes "Edit.F file.cs" as lead.
  complete = function(_, cmdline, cursorpos)
    local word = cmdline:sub(1, cursorpos):match('[Vv]sc%s+(%S*)$')
    if not word then return {} end
    local needle = word:lower()
    local starts, contains = {}, {}
    for _, name in ipairs(vs_command_names) do
      local at = name:lower():find(needle, 1, true)
      if at == 1 then starts[#starts + 1] = name
      elseif at then contains[#contains + 1] = name end
    end
    return vim.list_extend(starts, contains)
  end,
})
vim.cmd([[cnoreabbrev <expr> vsc (getcmdtype() == ':' && getcmdpos() <= 4) ? 'Vsc' : 'vsc']])

vim.api.nvim_create_user_command('Vsclist', function(opts)
  _G.vsneo.seeky('vscommands', opts.args)
end, { nargs = '*', desc = 'VSNeo: find and run a Visual Studio command' })
vs_abbrev('vsclist', 'Vsclist')

vim.api.nvim_create_user_command('Sethandler', function(opts)
  vim.rpcnotify(chan, 'vsneo_sethandler', opts.args)
end, { nargs = '+', desc = 'VSNeo: choose Visual Studio or Neovim ownership for a key' })
vs_abbrev('sethandler', 'Sethandler')

vim.api.nvim_create_user_command('VSNeoToggle', function(opts)
  local state = vim.trim(opts.args):lower()
  if state ~= '' and state ~= 'on' and state ~= 'off' then
    vim.api.nvim_echo({ { 'VSNeoToggle: expected on, off or nothing', 'ErrorMsg' } }, true, {})
    return
  end
  vim.rpcnotify(chan, 'vsneo_toggle', state)
end, {
  nargs = '?',
  complete = function() return { 'on', 'off' } end,
  desc = 'VSNeo: enable or disable Vim input without restarting Neovim',
})

-- :VSNeoInfo - the extension's diagnostics report (also Tools > VSNeo >
-- Diagnostics, which works before Neovim starts). rpcrequest blocks nvim
-- until the answer arrives; user-invoked, never on a key path.
vim.api.nvim_create_user_command('VSNeoInfo', function()
  local ok, report = pcall(vim.rpcrequest, chan, 'vsneo_info')
  if not ok then
    report = 'VSNeoInfo failed: ' .. tostring(report)
  end
  vim.api.nvim_echo({ { tostring(report), 'None' } }, true, {})
end, { desc = 'VSNeo: show extension, Neovim and session diagnostics' })

-- :Seeky [mode] [query] - the embedded picker, Telescope's :Telescope. The
-- query is the rest of the line, verbatim. From visual mode (:'<,'>Seeky
-- grep) with no query, the selection's first line is the query: a grep
-- pattern is one line, and taking the first is closer to intent than
-- refusing. 'lines' searches the current file (unsaved edits included);
-- 'resume' reopens the last picker exactly as it was left.
local SEEKY_MODES = { 'files', 'grep', 'lines', 'symbols', 'outline', 'git', 'mixed', 'resume',
  'buffers', 'oldfiles', 'marks', 'registers', 'diagnostics', 'keymaps', 'command_history',
  'search_history', 'vscommands' }
vim.api.nvim_create_user_command('Seeky', function(opts)
  local mode, query = opts.args:match('^(%S*)%s*(.*)$')
  if mode == '' then mode = 'files' end
  if query == '' and opts.range > 0 then
    -- With no visual selection ever made (:5Seeky grep), visualmode() is ''
    -- and getregion throws E475: take the range's first line instead.
    if vim.fn.visualmode() ~= '' then
      local region = vim.fn.getregion(vim.fn.getpos("'<"), vim.fn.getpos("'>"),
        { type = vim.fn.visualmode() })
      query = vim.trim(region[1] or '')
    else
      query = vim.trim(vim.fn.getline(opts.line1))
    end
  end
  _G.vsneo.seeky(mode, query)
end, {
  nargs = '*',
  range = true,
  desc = 'VSNeo: Seeky picker (files, grep, lines, symbols, outline, git, mixed, resume, buffers, '
    .. 'oldfiles, marks, registers, diagnostics, keymaps, command_history, search_history)',
  complete = function(lead, line)
    -- Only the first argument is a mode; the rest is free text.
    if line:match('^%S*Seeky%s+%S+%s') then return {} end
    return vim.tbl_filter(function(m) return vim.startswith(m, lead) end, SEEKY_MODES)
  end,
})

-- Vim's quickfix-refinement command is not built into every nvim version VSNeo
-- supports. Push the filtered list so :colder still returns to the unfiltered export.
vim.api.nvim_create_user_command('Cfilter', function(opts)
  local ok, regex = pcall(vim.regex, opts.args)
  if not ok then
    vim.api.nvim_echo({ { 'Cfilter: ' .. tostring(regex), 'ErrorMsg' } }, true, {})
    return
  end

  local current = vim.fn.getqflist({ title = true, items = true })
  local items = {}
  for _, entry in ipairs(current.items) do
    local matches = regex:match_str(entry.text or '') ~= nil
    if opts.bang then matches = not matches end
    if matches then items[#items + 1] = entry end
  end
  vim.fn.setqflist({}, ' ', { nr = '$', title = current.title, items = items })
end, { nargs = '+', bang = true, desc = 'VSNeo: filter quickfix entries by text (! rejects matches)' })

-- netrw cannot work here: its directory buffers are foreign buffers Visual
-- Studio can never show, and the snap-back would eat them. The plugin still
-- loads - plugins come from startup, before this script runs - so its
-- commands are overridden outright (force). The file explorer is Visual
-- Studio's Solution Explorer; SyncWithActiveDocument makes it select the
-- file being edited, which is what "explore from here" means. The split
-- variants collapse to the same target - Visual Studio has the one explorer.
local function solution_explorer()
  _G.vsneo.cmd('SolutionExplorer.SyncWithActiveDocument')
  _G.vsneo.cmd('View.SolutionExplorer')
end
for _, name in ipairs({ 'Explore', 'Ex', 'Vexplore', 'Sexplore', 'Hexplore', 'Texplore' }) do
  vim.api.nvim_create_user_command(name, solution_explorer,
    { force = true, desc = 'VSNeo: Visual Studio Solution Explorer' })
end

-- :e must never reach nvim itself: it would load the file into a buffer nvim
-- owns - Visual Studio never opens it, the mirror ignores its edits ("some
-- other document"), and the state pushes keep reporting positions from a file
-- nobody is showing. Worse, a later mirror for the same path hits E95 naming
-- its own buffer. Files are Visual Studio's to open. File.OpenFile focuses an
-- already-open document, so this doubles as :b; a bare :e reopens the current
-- file, which is where Visual Studio's own changed-on-disk prompt lives. The
-- bang is accepted and ignored: conflict decisions about the file belong to
-- Visual Studio. Same uppercase-plus-abbreviation shim as :Vsc, verified
-- against real nvim: the guards keep :s/e/x/ untouched.
vim.api.nvim_create_user_command('Edit', function(opts)
  local path = opts.args ~= '' and opts.args or vim.api.nvim_buf_get_name(0)
  if path == '' then return end
  -- Absolute, so a relative path resolves against nvim's cwd once, here,
  -- rather than against whatever directory Visual Studio happens to favour.
  local abs = vim.fn.fnamemodify(path, ':p')
  -- A directory is an explorer request (:e .), not a file open.
  if vim.fn.isdirectory(abs) == 1 then
    solution_explorer()
    return
  end
  _G.vsneo.cmd('File.OpenFile', abs)
end, { nargs = '?', bang = true, complete = 'file', desc = 'VSNeo: open file in Visual Studio' })
vs_abbrev('e', 'Edit') -- also after a |, for :w | e foo
vs_abbrev('edit', 'Edit')

-- :b resolves against nvim's buffer list but the target is opened in Visual
-- Studio. A buffer nvim loaded itself (:b with a path it read from disk) is
-- exactly what the mirror's adoption exists for, so this is safe either way.
-- Numbers and name substrings, like the real :b; a miss or a non-file buffer
-- is an error message (which ext_messages renders), never a desync.
vim.api.nvim_create_user_command('Buffer', function(opts)
  local arg = opts.args
  local byNumber = tonumber(arg)
  for _, b in ipairs(vim.api.nvim_list_bufs()) do
    local name = vim.api.nvim_buf_get_name(b)
    local hit = byNumber ~= nil and b == byNumber
      or (byNumber == nil and name ~= '' and name:lower():find(arg:lower(), 1, true) ~= nil)
    if hit then
      if name == '' then
        vim.api.nvim_err_writeln('E86: buffer ' .. arg .. ' is not a file Visual Studio can open')
        return
      end
      _G.vsneo.cmd('File.OpenFile', vim.fn.fnamemodify(name, ':p'))
      return
    end
  end
  vim.api.nvim_err_writeln('E86: buffer does not exist: ' .. arg)
end, { nargs = 1, complete = 'buffer', desc = 'VSNeo: open buffer in Visual Studio' })
vim.cmd([[cnoreabbrev <expr> b      (getcmdtype() == ':' && getcmdpos() <= 2) ? 'Buffer' : 'b']])
vim.cmd([[cnoreabbrev <expr> buffer (getcmdtype() == ':' && getcmdpos() <= 7) ? 'Buffer' : 'buffer']])

-- :bn/:bp walk Visual Studio's tabs, not nvim's buffer list - Visual Studio
-- is the window manager (same rule as the :split family), and its tab order
-- is the order you are looking at. The orders differ; the gesture matches.
vim.api.nvim_create_user_command('Bnext', function() _G.vsneo.cmd('Window.NextTab') end,
  { desc = 'VSNeo: next Visual Studio tab' })
vim.api.nvim_create_user_command('Bprevious', function() _G.vsneo.cmd('Window.PreviousTab') end,
  { desc = 'VSNeo: previous Visual Studio tab' })
vim.cmd([[cnoreabbrev <expr> bn        (getcmdtype() == ':' && getcmdpos() <= 3) ? 'Bnext' : 'bn']])
vim.cmd([[cnoreabbrev <expr> bnext    (getcmdtype() == ':' && getcmdpos() <= 6) ? 'Bnext' : 'bnext']])
vim.cmd([[cnoreabbrev <expr> bp        (getcmdtype() == ':' && getcmdpos() <= 3) ? 'Bprevious' : 'bp']])
vim.cmd([[cnoreabbrev <expr> bprevious (getcmdtype() == ':' && getcmdpos() <= 10) ? 'Bprevious' : 'bprevious']])

-- gf is deliberately NOT mapped: many configs bind it themselves (the sample
-- rc sends it to Edit.GoToFile), and native gf already lands on a real file
-- through the extension's follow logic - nvim loads the file, Visual Studio
-- opens or activates it. No key is stolen from nvim for it.

-- nvim ships its own gc/gcc mappings (since 0.10), so "unmapped" cannot tell
-- the user's gc from nvim's. Snapshot the mappings before the rc: one that
-- reads the same afterwards is nvim's and may be replaced.
local maps_before_rc = {}
for _, lhs in ipairs({ 'gc', 'gcc', '=', '==' }) do
  for _, mode in ipairs({ 'n', 'x' }) do
    maps_before_rc[mode .. lhs] = vim.fn.maparg(lhs, mode, false, true)
  end
end
