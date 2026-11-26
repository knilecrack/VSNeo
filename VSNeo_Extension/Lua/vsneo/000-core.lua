-- VSNeo's companion, running inside the embedded nvim.
--
-- Loaded from disk rather than embedded in the C# assembly. It used to be a
-- verbatim string literal, where every double quote had to be doubled - and a
-- comment mentioning E325 silently terminated the string and broke the build.
-- As a real file it gets syntax highlighting, needs no escaping, and can be
-- read on its own.
--
-- It still ships inside the VSIX beside the DLL, so it cannot drift out of step
-- with the rpcnotify contract the extension expects. This is not the user's
-- config; that is separate, opt-in (~/.vsneorc), and sourced at the bottom of
-- this file.
--
-- Receives the RPC channel id as its only argument.

local chan = ...

vim.cmd('filetype plugin indent on')

-- Visual Studio draws every character; nvim's highlighting is never seen.
-- It is not free, though: nvim still has a grid (ui_attach), redraws it on
-- every change, and computes syntax for the visible lines each time - regex
-- syntax for C# (cs.vim), Treesitter for Lua, Markdown and help - then ships
-- the resulting highlight attributes over the pipe to be skipped. Syntax is
-- off by default here for that reason. The cost is small: a few indent
-- scripts (Python's, HTML's) and matchit's % skip comments and strings only
-- when syntax is on. A ~/.vsneorc that sets g:vsneo_syntax = 1 and says
-- 'syntax on' gets it back - the rc is sourced after this line.
--
-- Treesitter needs its own switch: ftplugins start it (lua, markdown, help),
-- and starting it re-sets g:syntax_on as a side effect, so that variable
-- cannot tell "the user wants syntax" apart. Stopping it can restore legacy
-- syntax for the buffer, which is cleared again right after.
vim.cmd('syntax off')
vim.api.nvim_create_autocmd('FileType', {
  callback = function(args)
    -- 1 or true: every other switch accepts both, and vim.g.vsneo_syntax =
    -- true from a Lua rc used to do nothing.
    local s = vim.g.vsneo_syntax
    if s == 1 or s == true then return end
    if vim.treesitter.highlighter.active[args.buf] then
      pcall(vim.treesitter.stop, args.buf)
    end
    vim.bo[args.buf].syntax = ''
  end,
})

-- Visual Studio decides what wraps. If nvim wrapped as well its screen
-- lines would stop matching VS's, and H, M, L and the <C-d> family are
-- all defined in screen lines - they would drift by however many lines
-- nvim thought had wrapped.
vim.o.wrap = false

-- Vertical padding is synchronized with Visual Studio's real layout. Horizontal
-- padding remains disabled: VS owns horizontal scrolling and no corresponding
-- column-offset bridge exists.
vim.o.sidescrolloff = 0

-- Visual Studio keeps its first visible line when the view changes height
-- (a resize of the window; the old showcmd margin did it on every zz), and so
-- must nvim: the default 'cursor' re-scrolls the window on every resize.
vim.o.splitkeep = 'topline'

-- Nothing draws a status line either, and every row it occupies is a row
-- the text window does not have. Measured: with ext_cmdline on, a grid of
-- 30 gives a 29-line window by default and a 30-line window with this
-- off. Zero chrome means the viewport synchroniser can pass Visual
-- Studio's visible line count straight through, and <C-d> then scrolls by
-- what you can actually see.
vim.o.laststatus = 0

-- Visual Studio's outlining regions are mirrored here as manual folds (see
-- the fold section further down): manual means nvim never invents folds of
-- its own, and level 99 means the closed state is exactly what the sync says
-- it is rather than derived from a level. Any other foldmethod would
-- recompute folds nvim-side and the two editors would fight over them.
vim.wo.foldmethod = 'manual'
vim.wo.foldlevel = 99

-- Yank and put go through the system clipboard, so Vim's registers and Visual
-- Studio's Ctrl+C / Ctrl+V are the same thing. Without this, y in visual mode
-- fills a register nothing in Visual Studio can reach, and pasting into another
-- application quietly gets whatever was there before.
--
-- Safe to set unconditionally here: has('clipboard_working') is 1 on this
-- platform. Were there no provider, every yank would raise an error message
-- instead - and nothing renders those.
if vim.fn.has('clipboard_working') == 1 then
  vim.o.clipboard = 'unnamedplus'
end

-- nvim's own insert mappings (<C-W>, <C-U>, <Tab>, <S-Tab> ship with nvim),
-- captured before the user rc is sourced: vsneo_imaps pushes only what the
-- user added or redefined, so a key is claimed from Visual Studio's insert
-- mode only for people who actually mapped it. Plugins from pack/*/start
-- are already loaded by now and count as defaults here; a :packadd from the
-- rc lands after and counts as the user's.
-- The callback rides along: nvim 0.11+ ships Lua-callback defaults (<Tab>,
-- <S-Tab>, <C-S>) with no rhs, so rhs alone read a user's own callback
-- mapping on those keys as the default. nvim_get_keymap returns the same
-- function each call, so identity tells them apart.
local default_imaps = {}
for _, m in ipairs(vim.api.nvim_get_keymap('i')) do
  default_imaps[m.lhs] = { rhs = m.rhs or '', callback = m.callback }
end

-- 'inccommand' previews :s/ by really editing the buffer and reverting it.
-- Those previews arrive as buffer events carrying a null changedtick, and
-- nothing here renders them - so at best they are RPC on every keystroke of a
-- substitution, and at worst one missed guard writes a preview into the real
-- file, where it stays: nvim's revert is not a buffer change and produces no
-- event to undo it with. BufferMirror drops null-tick events regardless; this
-- stops them being sent at all.
vim.o.inccommand = ''

-- No swap files, and this one is not an optimisation. Naming a buffer
-- after a real path makes nvim treat it as a real file, so it looks for a
-- swap file, and on finding one it raises the modal E325 ATTENTION
-- prompt. Nothing renders that prompt, so nvim simply stops: mode()
-- reports 'r?', a confirm query, and every keystroke is swallowed
-- answering a question nobody can see. Visual Studio owns the file and
-- its recovery story; a second one here can only deadlock us.
vim.o.swapfile = false
vim.o.backup = false
vim.o.writebackup = false

-- Belt and braces: A suppresses the swap-file ATTENTION message even if
-- something contrives to create one.
vim.opt.shortmess:append('A')
vim.api.nvim_create_autocmd('BufWriteCmd', {
  pattern = '*',
  callback = function(ev)
    vim.bo[ev.buf].modified = false
  end,
})

local group = vim.api.nvim_create_augroup('VSNeo', { clear = true })

-- Set around a cursor move Visual Studio must not echo back to its caret:
-- note_viewport clamps nvim's cursor into the window while the real caret is
-- scrolled off it, and that bookkeeping is not a motion. Consumed by the next
-- push, whichever autocmd makes it - CursorMoved may fire inside the API call
-- or on a later event-loop turn, depending on the embed.
local synthetic_cursor = false

-- Set by vsneo.set_cursor around a caret push from Visual Studio. Setting the
-- cursor scrolls nvim's window to reveal it when the target sits outside the
-- window, and that scroll is transitional - the topline Visual Studio is
-- actually showing arrives through note_viewport a moment later. While set,
-- push() reports -1 as the topline: "no scroll information in this push".
local scroll_silent = false

-- The first visible line (1-based) Visual Studio last put nvim's window at
-- (set_cursor, set_topline, note_viewport). note_viewport's topline guard
-- accepts it beside the one Visual Studio last heard reported: the report of
-- this one may still be on the wire, and set_cursor's is never sent.
local vs_top = nil

-- Called after every write the mirror makes for Visual Studio, with the
-- buffer and its changedtick before and after. Assigned in the dot-repeat
-- section, which has to tell those writes from changes nvim made itself.
local mirror_wrote = nil

-- Called when a change happens that the dot-repeat capture never sees as
-- one: a Visual Studio operator (vsneo.operator) runs as g@, which moves no
-- changedtick, and its edit comes back as a mirror write - so the insert
-- change before it stayed current, and . replayed that instead of gc.
-- Assigned in the dot-repeat section; forgetting the change hands . back to
-- native redo, which holds the g@.
local dot_forget = nil

-- Fold mirroring. Visual Studio's outlining regions are recreated here as
-- manual folds, and the closed state is kept identical on both sides. This is
-- the last agreed state as a flat {start, end, closed} triple list (1-based
-- lines, closed a boolean) - the echo guard: a push that changes nothing is
-- dropped, whichever side it originated from.
local agreed_folds = {}

-- Set when a whole-buffer replacement may have left manual folds behind that
-- agreed_folds no longer describes (nvim keeps them when the line count is
-- unchanged). The next folds_set rebuilds even if its list is empty and so
-- compares equal to the reset one; apply_folds clears it.
local folds_dirty = false

-- changedtick at the last agreed-state sync. Line edits shift every fold
-- (both sides track them), but this list's boundaries stay where they were,
-- so comparing after an edit reports phantom opens and deletes - an edit
-- above a collapsed region expanded it. Detection stays silent until Visual
-- Studio's debounced resync refreshes the boundaries (and this tick).
local agreed_tick = -1

