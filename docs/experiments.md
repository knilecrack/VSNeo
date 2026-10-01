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

**Status.** Steps 2 and 3 below exist on the branch
`experiment/insert-via-nvim` behind `vim.g.vsneo_insert_via_nvim`: all
insert-mode typing (letters, Enter, Backspace, Tab, Delete) goes through
`nvim_input` when the flag is on, with the IntelliSense gate kept. Step 1,
the completion trigger, is not attempted there - with the flag on,
completion does not pop, which is the thing to feel before deciding
whether step 1 is worth doing.

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
