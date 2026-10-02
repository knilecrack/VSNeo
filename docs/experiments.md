# Experiments worth trying

Design directions that are not settled. Each entry says what the change is,
what it would buy, what it would cost, and how to find out cheaply. The
architecture itself (Visual Studio keeps the editor, nvim is the brain, a
mirrored buffer between them, keys routed by a locally cached mode) is not up
for debate here: it is the same shape as vscode-neovim, and its known pain
points are inherent to the approach rather than to this implementation. See
"Considered and rejected" at the end for the alternatives that were weighed.

## Insert mode through nvim

**Today.** Insert mode passes through to Visual Studio untouched, so
IntelliSense, snippets and brace completion keep working. nvim never sees the
typed text as keys; it learns of it as buffer spans from the mirror. Everything
that Vim derives from insert-mode keystrokes therefore has to be reconstructed
on the nvim side: `.` for a change that went through insert (the companion
recovers the change keys from `vim.on_key` and reads the inserted text as a
buffer slice), macros (`q` records `cw<Esc>`; the register is rewritten from a
key log after `RecordingLeave`), `vsneo.multi_edit()`, and the held-keys window
behind a pending remote edit. Each of those is a heuristic with a documented
failure mode (a caret that jumps mid-insert, a capture over 500 bytes, a
register the log cannot reproduce byte for byte).

The one case that already routes insert-mode typing through nvim is the
shadow of a pending remote edit (`BufferMirror.HasUnappliedRemoteEdits`):
typed characters go through `nvim_input`, nvim inserts them, and the text
comes back as an ordered remote edit. That path works and is exercised on
every `cw`. It is the seed of this experiment.

**The change.** Route *all* insert-mode typing through `nvim_input`, per
buffer or per session, as an opt-in mode. nvim becomes the single writer in
insert mode; Visual Studio receives the text as remote edits, the same way it
receives an `x` or a `dd` today.

**What it buys.**

- `.`, macros and multi-edit become native. The reconstruction code in
  `vsneo.lua` (dot-repeat capture, the macro register rewrite) and the
  routing-behind-remote-edits special case can go, along with their failure
  modes.
- One writer per buffer in every mode. The echo detection in
  `ApplyRemoteLines` shrinks to the tick and in-flight accounting, since
  Visual Studio no longer originates edits while typing.
- nvim's insert-mode features work as in Vim: `<C-r>{reg}`, `<C-a>`, `<C-t>`
  / `<C-d>`, `<C-x>` completion of nvim's own, abbreviations, insert-mode
  mappings with printable lhs (`imap jk <Esc>` - impossible today because typed
  text reaches Visual Studio, never nvim), `'textwidth'`, `'formatoptions'`.

**What it costs.**

- **IntelliSense does not trigger on buffer changes.** Visual Studio's
  completion, signature help and brace completion fire on the typed-character
  *command* (`VSStd2K.TYPECHAR`) going through the view's command chain, not
  on `ITextBuffer.Changed`. Text arriving as a remote edit pops nothing. To
  keep them, the extension would have to synthesize the equivalent after each
  echo lands: send the character to the view's `IOleCommandTarget` as a
  TYPECHAR *instead of* inserting it, with the buffer already holding it (so
  the command must not insert again), or drive `IAsyncCompletionBroker`
  directly with a trigger. Neither is a known-good path; this is the part to
  prototype first.
- **Latency.** A typed character travels VS -> pipe -> nvim -> pipe -> VS
  before it appears. The key->caret figures in the log (p50 about 1.4 ms, p95
  under 5 ms) say the round trip is not the problem; the UI-thread queue is,
  which is why remote edits post at `UiPriority.KeyResponse`. Fast typing
  would ride the same path as `cw` typing does today. Measure with
  `key->caret` and `slow ui` lines before judging.
- **Everything Visual Studio does on typing.** Auto-indent on Enter (a routed
  Enter takes nvim's indenting today, documented), automatic brace and quote
  closing, snippet expansion on Tab, XML doc comment continuation, format-on-
  type (`}` and `;` in C#). Each is either lost, or re-triggered by the
  synthesized command above, or reimplemented on the nvim side (`'indentexpr'`
  is not Roslyn's formatter).
- **Undo granularity.** Remote edits are grouped into undo transactions by
  the mirror; a whole insert session would arrive as many small edits and
  needs to coalesce into one transaction per insert, matching what Vim's `u`
  undoes.

**How to find out cheaply.**

1. Prototype the trigger question alone, without changing routing: with a
   buffer edited by a remote edit (type `cw` then a word, which already
   routes through nvim), try to make completion appear by sending a TYPECHAR
   for the last character through `VsNeoCommandFilter`'s next target with the
   text already in the buffer. If that either double-inserts or does not
   trigger, try `IAsyncCompletionBroker.TriggerCompletion` with a typed-char
   trigger. This decides whether the experiment is viable at all.
2. Add `vim.g.vsneo_insert_via_nvim` (default off). In
   `VsNeoKeyProcessor`, when set, route insert-mode `TextInput` through
   `session.Input` the way the remote-edit shadow already does, and route
   Enter, Backspace, Tab and Delete through `TryRouteBehindRemoteEdits`
   unconditionally. Keep the IntelliSense gate: while a list is open, keys
   still go to Visual Studio.
3. Turn off the reconstruction paths under the flag (dot-repeat capture,
   macro rewrite) and check `.` and `qa...q` natively.
4. Measure typing feel with the log's percentiles on a large file, and count
   `slow ui` lines during a minute of fast typing.
5. Decide per feature what to do about auto-indent, brace completion and
   snippets. A plausible end state: the flag is per filetype, on for plain
   text and configuration files, off for C# where Roslyn's typing services
   matter most.

If step 1 fails, the experiment is dead and this entry should say so.

**Status: built, opt-in, not yet verified live** (branch
`feat/insert-via-nvim`; `vim.g.vsneo_insert_via_nvim`, `:VSNeoInsertViaNvim`).
Steps 1-3 were done together rather than in order, so step 1's answer is
still pending a live run. What exists:

- Routing. `VsNeoKeyProcessor.TryRouteInsertTyping` sends insert/replace
  text to nvim; `VsNeoCommandFilter.TryRouteInsertViaNvim` sends Enter, Tab,
  S-Tab, Backspace, Delete, the arrows, Home/End, PageUp/PageDown and
  Ctrl+arrows. Vim's insert chords (`<C-r>`, `<C-t>`, `<C-d>`, `<C-u>`,
  `<C-a>`, `<C-v>`, `<C-k>`, `<C-e>`, `<C-y>`, `<C-w>`) go to nvim too.
  Shift-selection keys stay Visual Studio's.
- Completion (step 1). `Editor/RoutedTyping.cs` matches each routed key to
  the remote edit that lands it (the line-diff's inserted tail), then does
  what the editor's TYPECHAR handler does: `TriggerCompletion` when no
  session is open, `OpenOrUpdate(Insertion, c)` either way. Backspace
  updates an open list as a Deletion. With an async list open, letters
  still route (the list filters after they land); a character for which
  `ShouldCommit` says yes, Enter, Tab and the arrows go to Visual Studio,
  and the commit is a Visual Studio edit. A legacy list (no update API)
  takes all typing.
- Caret. In insert the caret follows nvim's cursor as in normal mode
  (`ApplyPendingCore`), holding only while remote edits are unapplied. A
  caret move caused by an nvim edit landing is not echoed back
  (`IsRemoteDisplacement`: `BufferMirror.IsApplyingRemoteEdit` /
  `LastRemoteVersion`); a mouse click still is. The forced caret push
  before `<Esc>`/`<C-o>` is skipped.
- Undo (the cost listed above). `Editor/UndoGroups.cs` counts
  `UndoTransactionCompleted` (added, not merged) per insert session and `u`
  undoes the group's count; no transaction is held open, which is what broke
  1.6.2. Ctrl+Z or any undo not ours drops the counts (single steps until
  the next insert). If the shell adapter never raises the event, `u` stays
  per-character: the log shows `undo group of N` when it works.
- `.` and macros (step 3). Not switched off but made self-correcting: at
  insert leave the companion compares the captured slice with `getreg('.')`.
  Equal means every character arrived as a key, and `.` / the register use
  nvim's own record (`change.native`, `session.native`); different (Visual
  Studio committed a completion inside the insert) falls back to the
  reconstruction, which is still right. `tests/insert_via_nvim_tests.lua`.

**Live result (C#, 2026-10-02): worse than the default; keep it off for
code.** Completion works once it is driven from one side: identifier
characters must route to nvim even while a list is open (sending them to
Visual Studio left an nvim-opened list unfiltered), and remote edits must be
narrowed to the changed characters (`RemoteLineEdit.Narrow`): a whole-line
replace cost 20-50 ms per key, displaced the caret, and collapsed the list's
tracking span so it dismissed itself. What cannot be had is everything else
Visual Studio does on the TYPECHAR command: brace completion on `(` and `{`,
format-on-type (`}` closing a full property stays unformatted), signature
help. Those services hook the command chain, not buffer changes, and expose
no trigger API the way completion does - getting them back means routing the
punctuation to Visual Studio, i.e. two writers again, at which point `.`
and macros fall back to the reconstruction in nearly every line of code and
the experiment buys nothing over the default. Typing also felt slower:
key->caret p50 ~9 ms against ~6 ms with it off, p95 up to 46 ms before the
narrowing fix. Where it fits: per buffer (`vim.b.vsneo_insert_via_nvim` from
an ftplugin) for prose and config files - Markdown, text, YAML, scripts -
where Vim's insert mode wins and Visual Studio's typing services do little.
A hybrid (letters through nvim, punctuation through Visual Studio) was
considered and not built, for the reason above.

**Before merging: the file types it is meant for.** Set
`vim.g.vsneo_insert_via_nvim = { 'markdown', 'text', 'yaml', 'json', 'xml',
'lua', 'ps1', 'gitcommit' }` (or `:VSNeoInsertViaNvim buffer on` in one
document) and check `:VSNeoInsertViaNvim` reports ON in each. Per file type:

1. Plain typing, fast, several lines: no letter out of place, no
   `mirror drifted` in the log, `key->caret` close to the default's.
2. Enter: nvim's indenting is acceptable (`'autoindent'`, the filetype's
   `indentexpr`) - YAML and JSON nesting in particular.
3. Visual Studio's completion where the language service has one (JSON
   schema, XML, PowerShell): opens, filters, Tab/Enter commit, and typing
   after a commit continues at the right place.
4. `cw` + text + `.`; `qa` + an insert + `q`, `@a`; `<C-r>"`; `<C-w>`.
5. `u` undoes a whole insert (log: `undo group of N`).
6. Switch between a listed file and a C# file: C# keeps brace completion
   and formatting (the switch follows the current buffer).
7. Mouse click mid-insert, then keep typing: text lands at the click.

Merge when those hold for the listed types; C# stays off by default.

Not done: signature help on `(` and `,` (Roslyn's opens on the TYPECHAR
command; candidate: run `Edit.ParameterInfo` after `(` lands), brace
completion, snippet expansion, format-on-type, smart indent. Things to watch
in the first live run: `key->caret` p50/p95 while typing, `slow ui` lines
from `RoutedTyping.Trigger`, whether the list opens on the first letter of
an identifier in C#, whether `u` logs `undo group of N`, and whether a fast
burst of typing ever lands a letter out of place (caret displacement echoed
back).

## Considered and rejected

Kept here so the reasoning is not lost.

- **Render nvim's grid inside Visual Studio** (a `ui_attach` renderer in a
  tool window or editor pane). Full fidelity, no sync, and no Roslyn in the
  text: no IntelliSense, refactorings, CodeLens, debugger data tips. It makes
  Visual Studio a window manager for nvim.
- **Reparent Neovide's window.** Same trade as above with a better renderer.
- **Reimplement Vim in C#** (VsVim's way). No process, no mirror, and
  fidelity stalls at what the author had time for; configs, plugins and the
  command line never work.
- **nvim as the host, Visual Studio following.** Type in a real nvim, Visual
  Studio reloads the file on save and provides IDE features through commands.
  Perfect Vim, IDE features only at checkpoints, and Roslyn's own edits
  round-trip through disk.
- **nvim plus the Roslyn language server, no Visual Studio.** Not an
  alternative to VSNeo but the reason it exists: whoever needs the debugger,
  designers, profilers, Live Share or the test explorer cannot leave.
- **Ask nvim per keystroke instead of caching the mode.** Always correct, and
  a round trip per key. The local decision is where the ~1 ms key->caret
  comes from.
- **One nvim window per document.** Simpler switching; but jumplist and the
  alternate file are per window in Vim and users expect them to span files
  (see `TextViewCreationListener.Attach`).
