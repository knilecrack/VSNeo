" ~/.vsneorc — VsNeo user configuration, ported from .vsvimrc (VsVim).
"
" Install: copy this file to %USERPROFILE%\.vsneorc and restart Visual Studio.
"
" What is different from VsVim, and why:
"
"   * ':Vsc Some.Command' runs ANY Visual Studio command by its
"     Tools > Options > Keyboard name - VsVim's command set included, and a
"     lowercase ':vsc' typed at the start of the command line expands to it
"     like in VsVim. This file uses the ':Vsc' spelling throughout.
"
"   * Mappings run commands with <Cmd>...<CR>, not :...<CR>. A ':' mapping
"     really opens the command line: every press flips the mode to
"     command-line and back, which redraws the mode badge, fires the
"     mode-change cursor effects and can flash the command-line popup.
"     <Cmd> runs the command without leaving the mode. In visual mode it
"     also keeps the selection Visual Studio acts on, where ':' would
"     insert '<,'> - and :Vsc takes no range, so a visual ':Vsc' mapping
"     fails with E481. Keep ':' only where the range is the point (:s and
"     :m over a selection) or the command line is meant to stay open.
"
"   * Insert mode belongs to Visual Studio. Typed characters never reach
"     nvim, so 'inoremap' on printable keys (jj/jk to escape, 'imap a A')
"     can never fire and is omitted on purpose. Insert mappings on NAMED
"     keys work: a single <...> lhs (<Left>, <End>, <C-Home>, ...) is
"     claimed from Visual Studio and fed to nvim, which runs the mapping
"     itself. VS's own insert-mode editing (IntelliSense, Copilot
"     Tab-accept, Ctrl+V paste) keeps working natively for every key you
"     do not map.
"
"   * 'set number', 'relativenumber', 'cursorline', 'scrolloff', 'ttimeout',
"     'guicursor' are gone: nvim is headless and renders nothing, and
"     scrolloff in particular would desync the viewport synchroniser (VsNeo
"     re-forces it to 0 after this file loads anyway).
"
"   * Ctrl+Alt(+Shift) chords are never sent to nvim - that is AltGr on many
"     layouts and VS's own binding namespace. The multi-caret chords
"     (<C-A-,>, <C-A-;>, <C-A-n>) therefore go straight to Visual Studio;
"     bind Edit.InsertCaretBelow/Above/NextMatchingCaret there if you want them.
"
"   * Copilot 'accept' macros ('i<Tab><Esc>') are gone: executed inside nvim
"     they would insert a literal tab through the buffer mirror instead of
"     accepting the suggestion. In insert mode VS's own Tab already accepts.
"
"   * Duplicate left-hand sides were resolved to their LAST definition, which
"     is what VsVim did with the same file. Notably:
"       <leader>a   -> Edit.GoToAll        (the Copilot-accept macro is dropped)
"       <leader>.   -> View.QuickActions   (was also NextDocumentWindow, append '.')
"       <leader>,   -> append ','          (was also PreviousDocumentWindow)
"       <leader>sw  -> Edit.FindInFiles    (normal mode; visual mode stays SurroundWith)
"       <leader>gc  -> Team.Git.Commit     (was also GenerateConstructor)
"       <leader>bd  -> CloseDocumentWindow (was also DisableAllBreakpoints)
"       <leader>tl  -> View.TaskList       (was also RepeatLastRun)
"       <leader>rf  -> Edit.GoToRecentFile (was also Edit.Replace)
"       <leader>tt  -> ViewTypeHierarchy   (was also View.Terminal)
"       <leader>gb  -> Team.Git.ManageBranches (was also Edit.GoToBase)
"       <leader>gp  -> Team.Git.Pull       (was also PeekDefinition)
"       n / N       -> nzzzv / Nzzzv       (was also nzz / Nzz)
"     Two typos in the original were fixed: '<scr>' -> '<CR>' on
"     ReorderParameters, and a stray 'VsVim' suffix on <leader>ww.
"
"   * Re-sourcing this file works (:source ~/.vsneorc, or <leader>vs below),
"     but VsNeo's invariant re-assert only runs at startup - if you experiment
"     with scrolloff/wrap, restart VS to get back to a known-good state.


" Search. nvim owns the regex engine, and VsNeo's search-highlight bridge reads
" getreg('/') + 'hlsearch', so these all do real work here.
set ignorecase
set smartcase
set hlsearch
set incsearch

" Indentation, used by nvim's own >> << == operators on the mirrored buffer.
set tabstop=4
set shiftwidth=4
set softtabstop=4
set expandtab
set autoindent

set backspace=eol,start,indent
set nostartofline
set magic
set selection=inclusive

" These three groups are read by VsNeo and drive what Visual Studio draws:
" Search = every hlsearch match, CurSearch = the match under the cursor,
" IncSearch = the yank flash. Defaults follow nvim's own; uncomment to taste.
" highlight Search    guibg=#3e68d7
" highlight CurSearch guibg=#ff9e64
" highlight IncSearch guibg=#ff9e64

nnoremap <SPACE> <Nop>
let mapleader=" "

" F1 help and VS's C-j/C-k are dead in normal/visual mode. (The .vsvimrc's
" inoremap versions could be added - F1 and C-j/C-k are named keys, which
" CAN be mapped in insert here - but <nop> in insert is pointless.)
nnoremap <F1> <nop>
vnoremap <F1> <nop>
nnoremap <C-j> <nop>
vnoremap <C-j> <nop>
nnoremap <C-k> <nop>
vnoremap <C-k> <nop>

" :Flash was a VsVim-side plugin command; no equivalent exists in VsNeo.
" nmap <leader>z <Cmd>Flash<CR>

" Surround the word under the cursor with a delimiter.
nnoremap <leader>s) ciw(<C-r>")<Esc>
nnoremap <leader>s] ciw[<C-r>"]<Esc>
nnoremap <leader>s} ciw{<C-r>"}<Esc>
nnoremap <leader>s> ciw<lt><C-r>"><Esc>
nnoremap <leader>s" ciw"<C-r>""<Esc>
nnoremap <leader>s' ciw'<C-r>"'<Esc>
nnoremap <leader>sw) ciW(<C-r>")<Esc>
nnoremap <leader>sw] ciW[<C-r>"]<Esc>
nnoremap <leader>sw} ciW{<C-r>"}<Esc>
nnoremap <leader>sw> ciW<lt><C-r>"><Esc>
nnoremap <leader>sw" ciW"<C-r>""<Esc>
nnoremap <leader>sw' ciW'<C-r>"'<Esc>

" Surround the visual selection with a delimiter.
vnoremap <leader>S" c"<C-r>""<Esc>
vnoremap <leader>S' c'<C-r>"'<Esc>
vnoremap <leader>S) c(<C-r>")<Esc>
vnoremap <leader>S] c[<C-r>"]<Esc>
vnoremap <leader>S} c{<C-r>"}<Esc>
vnoremap <leader>S> c<lt><C-r>"><Esc>
vnoremap <leader>S* c/*<C-r>"*/<Esc>

" Type a delimiter for splitting the line into separate lines.
nnoremap <Leader><Leader>\ :s//&\r<Left><Left><Left><Left>
xnoremap <Leader><Leader>\ :s//&\r<Left><Left><Left><Left>

nnoremap <Esc> <Cmd>nohlsearch<CR>

" Reload this file.
nnoremap <leader>vs <Cmd>source ~/.vsneorc<CR><Cmd>echo "vsneorc reloaded"<CR>
map zl <Cmd>source ~/.vsneorc<CR><Cmd>echo "vsneorc reloaded"<CR>

nnoremap <leader>a <Cmd>Vsc Edit.GoToAll<CR>
nnoremap <leader>y "+y
vnoremap <leader>y "+y

" inoremap jj/jk <ESC> intentionally omitted: printable insert-mode keys
" belong to VS. Named keys can be mapped in insert, though - this style
" drops out of insert when you navigate instead of typing (uncomment):
" imap <left>    <esc>
" imap <right>   <esc><right>
" imap <up>      <esc><up><right>
" imap <down>    <esc><down><right>
" imap <home>    <esc>0
" imap <end>     <esc><end>

" Keep search results centered (last-wins resolution of n/N/*/#).
nnoremap n nzzzv
nnoremap N Nzzzv
nnoremap * *zzzv
nnoremap # #zzzv

nnoremap Y y$

" Replace the visual selection with the default register without yanking it.
vnoremap <leader>p "_dP

" Document window and tab group navigation.
nnoremap <leader>mp <Cmd>Vsc Window.MoveToPreviousTabGroup<CR>
nnoremap <leader>mx <Cmd>Vsc Window.MoveToNextTabGroup<CR>

nnoremap <leader>sg <Cmd>Vsc SeekyVS.SeekyLiveGrepCommand<CR>
nnoremap <leader>vf <Cmd>Vsc SeekyVS.SeekyLiveGrepCommand<CR>

" Keep scrolling centered.
nnoremap <C-d> <C-d>zzzv
nnoremap <C-u> <C-u>zzzv

" Keep joins centered, cursor on the front.
nnoremap J mzJ`z

" Move lines up/down. Insert-mode versions omitted (insert passes through).
nnoremap <A-]> <Cmd>m .+1<CR>==
nnoremap <A-[> <Cmd>m .-2<CR>==
vnoremap <A-Down> :m '>+1<CR>gv=gv
vnoremap <A-Up> :m '<-2<CR>gv=gv

" Easier indent in visual mode: stay in visual mode.
vnoremap < <gv
vnoremap > >gv

" Toggles.
noremap <leader>ln <Cmd>Vsc Edit.ToggleLineNumbers<CR>
noremap <leader>wws <Cmd>Vsc Edit.ViewWhiteSpace<CR>

" Line-ending punctuation helpers.
noremap <leader>. <Cmd>Vsc View.QuickActions<CR>
noremap <leader>, :s/\v\s*(,\s*)*$/,/<CR>:nohl<CR>
noremap <leader>; :s/\v\s*(;\s*)*$/;/<CR>:nohl<CR>
noremap <leader>x :s/.\{1}$//<CR>:nohl<CR>

" Pinning and tab management.
noremap <leader>wp <Cmd>Vsc Window.PinTab<CR>
noremap <leader>wca <Cmd>Vsc Window.CloseAllButPinned<CR>
nnoremap <A-.> <Cmd>Vsc Window.NextTab<CR>
nnoremap <A-,> <Cmd>Vsc Window.PreviousTab<CR>
nnoremap <leader>c <Cmd>Vsc Window.Close<CR>

" Window navigation.
nnoremap <leader>wf <Cmd>Vsc FullScreen<CR>
nnoremap <leader>wc <Cmd>Vsc Window.CloseDocumentWindow<CR>
nnoremap <leader>wj <Cmd>Vsc Window.NextDocumentWindow<CR>
nnoremap <leader>wk <Cmd>Vsc Window.PreviousDocumentWindow<CR>
nnoremap <c-w><c-f> <Cmd>Vsc FullScreen<CR>
nnoremap <c-w><c-c> <Cmd>Vsc Window.CloseDocumentWindow<CR>
nnoremap <c-w><c-j> <Cmd>Vsc Window.NextDocumentWindow<CR>
nnoremap <c-w><c-k> <Cmd>Vsc Window.PreviousDocumentWindow<CR>

xnoremap <leader>sw <Cmd>Vsc Edit.SurroundWith<CR>
nnoremap <leader>is <Cmd>Vsc Edit.InsertSnippet<CR>

" Comment / uncomment.
nnoremap <leader>cc V<Cmd>Vsc Edit.CommentSelection<CR>
xnoremap <leader>cc <Cmd>Vsc Edit.CommentSelection<CR>
nnoremap <leader>CC V<Cmd>Vsc Edit.UncommentSelection<CR>
xnoremap <leader>CC <Cmd>Vsc Edit.UncommentSelection<CR>

nnoremap <leader>fif <Cmd>Vsc Edit.FindInFiles<CR>
nnoremap <leader><CR> <Cmd>nohlsearch<CR>

" K - quick info and parameter details. Overrides VsNeo's built-in K.
nnoremap K <Cmd>Vsc Edit.QuickInfo<CR><Cmd>Vsc Edit.ParameterInfo<CR>

" Improve navigation when wrapping: VS owns wrap, so j/k follow screen lines.
nnoremap j gj
nnoremap k gk

" Navigation history.
noremap <C--> <Cmd>Vsc View.NavigateBackward<CR>
noremap <C-=> <Cmd>Vsc View.NavigateForward<CR>

" Goto commands.
nnoremap gd <Cmd>Vsc Edit.GotoDefinition<CR>
nnoremap <leader>d <Cmd>Vsc Edit.GotoDeclaration<CR>
nnoremap gi <Cmd>Vsc Edit.GoToImplementation<CR>
nnoremap gf <Cmd>Vsc Edit.GoToFile<CR>
nnoremap <leader>f <Cmd>Vsc Edit.GoToFile<CR>
nnoremap gt <Cmd>Vsc Edit.GoToType<CR>
nnoremap gT <Cmd>Vsc Edit.GotoTypeDefinition<CR>
nnoremap <leader>rf <Cmd>Vsc Edit.GoToRecentFile<CR>
nnoremap <leader>sf <Cmd>Vsc EditorContextMenus.CodeWindow.ToggleHeaderCodeFile<CR>

noremap <leader>ff <Cmd>Vsc Edit.GoToFile<CR>
noremap <leader>fm <Cmd>Vsc Edit.GoToMember<CR>
noremap <leader>fw <Cmd>Vsc Edit.GoToAll<CR>
noremap <leader>gs <Cmd>Vsc Edit.GoToSymbol<CR>
noremap <leader>gco <Cmd>Vsc Copilot.ToggleCompletions<CR>
noremap <leader>gcp <Cmd>Vsc Copilot.ToggleCompletions<CR>

" Refactor.
nnoremap <leader>rn <Cmd>Vsc Refactor.Rename<CR>
xnoremap <leader>rn <Cmd>Vsc Refactor.Rename<CR>
xnoremap <leader>rem <Cmd>Vsc Refactor.ExtractMethod<CR>
nnoremap <leader>rem <Cmd>Vsc Refactor.ExtractMethod<CR>
xnoremap <leader>rrp <Cmd>Vsc Refactor.RemoveParameters<CR>
nnoremap <leader>rrp <Cmd>Vsc Refactor.RemoveParameters<CR>
xnoremap <leader>rop <Cmd>Vsc Refactor.ReorderParameters<CR>
nnoremap <leader>rop <Cmd>Vsc Refactor.ReorderParameters<CR>

" Code generation.
nnoremap <leader>gh <Cmd>Vsc EditorContextMenus.CodeWindow.GenerateEqualsAndGetHashCode<CR>

" Tests.
noremap <leader>tr <Cmd>Vsc TestExplorer.RunSelectedTests<CR>
noremap <leader>ta <Cmd>Vsc TestExplorer.RunAllTests<CR>
noremap <leader>tf <Cmd>Vsc TestExplorer.RunFailedTests<CR>
noremap <leader>td <Cmd>Vsc TestExplorer.DebugSelectedTests<CR>
noremap <leader>tss <Cmd>Vsc TestExplorer.ShowTestExplorer<CR>
noremap <leader>tsc <Cmd>Vsc View.CodeCoverageResults<CR>

" Breakpoints. <leader>bd resolves to CloseDocumentWindow below (last wins).
noremap <leader>be <Cmd>Vsc Debug.EnableAllBreakpoints<CR>
noremap <leader>br <Cmd>Vsc Debug.DeleteAllBreakpoints<CR>
noremap <leader>ba <Cmd>Vsc Debug.Breakpoints<CR>

" Build / debug.
noremap <leader>sb <Cmd>Vsc Build.BuildSolution<CR>
noremap <leader>sc <Cmd>Vsc Build.CleanSolution<CR>
noremap <leader>sbs <Cmd>Vsc Build.BuildSelection<CR>
noremap <leader>scs <Cmd>Vsc Build.CleanSelection<CR>
noremap <leader>sd <Cmd>Vsc Debug.Start<CR>
noremap <leader>sr <Cmd>Vsc Debug.StartWithoutDebugging<CR>
noremap <leader>sbc <Cmd>Vsc Build.Cancel<CR>
noremap <leader>sdc <Cmd>Vsc Debug.StopDebugging<CR>
nnoremap <Leader>qw <Cmd>Vsc Debug.QuickWatch<CR>
nnoremap <C-Left> <Cmd>Vsc Debug.SetNextStatement<CR>
nnoremap <C-Right> <Cmd>Vsc Debug.StepOver<CR>
nnoremap <C-Down> <Cmd>Vsc Debug.StepInto<CR>
nnoremap <C-Up> <Cmd>Vsc Debug.StepOut<CR>

" Solution explorer / blame.
nnoremap <leader>e <Cmd>Vsc View.SolutionExplorer<CR>
nnoremap <leader>lb <Cmd>Vsc Git.Blame<CR>

" PeasyMotion bindings (work if the extension is installed).
noremap ,, <Cmd>Vsc Tools.InvokePeasyMotion<CR>
noremap <leader>ls <Cmd>Vsc Tools.InvokePeasyMotionLineJumptoWordBegining<CR>
noremap <leader>le <Cmd>Vsc Tools.InvokePeasyMotionLineJumpToWordEnding<CR>
noremap ,t <Cmd>Vsc Tools.InvokePeasyMotionJumpToDocumentTab<CR>
nnoremap ;l <Cmd>Vsc Tools.InvokePeasyMotionJumpToLineBegining<CR>
nnoremap ;c <Cmd>Vsc Tools.InvokePeasyMotionTwoCharJump<CR>

" Visual Assist.
noremap <leader>ms <Cmd>Vsc VAssistX.FindSelected<CR>
noremap <leader>gD <Cmd>Vsc View.ClassViewShowDerivedTypes<CR>
noremap <C-w>v <Cmd>Vsc Window.NewVerticalTabGroup<CR>
noremap <leader>np <Cmd>Vsc OtherContextMenus.UITestEditorContextMenu.Splitintoanewmethod<CR>
noremap <leader>ww <Cmd>Vsc Window.Windows<CR>

" Quick Actions (lightbulb).
nnoremap <leader>qa <Cmd>Vsc View.QuickActions<CR>

" Format document / selection.
nnoremap <leader>fd <Cmd>Vsc Edit.FormatDocument<CR>
vnoremap <leader>fd <Cmd>Vsc Edit.FormatSelection<CR>

" Organize usings.
nnoremap <leader>ou <Cmd>Vsc Edit.RemoveAndSort<CR>

" Navigate errors/warnings.
nnoremap ]e <Cmd>Vsc View.NextError<CR>
nnoremap [e <Cmd>Vsc View.PreviousError<CR>
nnoremap ]d <Cmd>Vsc Edit.GoToNextIssueinFile<CR>
nnoremap [d <Cmd>Vsc Edit.GoToPreviousIssueinFile<CR>

" Navigate methods/members.
nnoremap ]m <Cmd>Vsc Edit.NextMethod<CR>
nnoremap [m <Cmd>Vsc Edit.PreviousMethod<CR>

" Peek definition / implementation. <leader>gp resolves to Git Pull below.
nnoremap gp <Cmd>Vsc Edit.PeekDefinition<CR>
nnoremap gP <Cmd>Vsc Edit.PeekImplementation<CR>

" Copilot Chat.
nnoremap <leader>ai <Cmd>Vsc GitHub.Copilot.Chat.ToggleChatWindow<CR>
nnoremap <leader>ae <Cmd>Vsc Explain<CR>
vnoremap <leader>ae <Cmd>Vsc Explain<CR>
nnoremap <leader>af <Cmd>Vsc GitHub.Copilot.Chat.Fix<CR>
vnoremap <leader>af <Cmd>Vsc GitHub.Copilot.Chat.Fix<CR>
nnoremap <leader>ad <Cmd>Vsc GitHub.Copilot.Chat.Doc<CR>
vnoremap <leader>ad <Cmd>Vsc GitHub.Copilot.Chat.Doc<CR>

" Bookmarks.
nnoremap <leader>mt <Cmd>Vsc Edit.ToggleBookmark<CR>
nnoremap <leader>mn <Cmd>Vsc Edit.NextBookmark<CR>
nnoremap <leader>mN <Cmd>Vsc Edit.PreviousBookmark<CR>
nnoremap <leader>mc <Cmd>Vsc Edit.ClearBookmarks<CR>
nnoremap <leader>ma <Cmd>Vsc View.BookmarkWindow<CR>

" Sync Solution Explorer with the active document, error list, output.
nnoremap <leader>el <Cmd>Vsc SolutionExplorer.SyncWithActiveDocument<CR>
nnoremap <leader>er <Cmd>Vsc View.ErrorList<CR>
nnoremap <leader>to <Cmd>Vsc View.Output<CR>

" Extract interface.
nnoremap <leader>rei <Cmd>Vsc Refactor.ExtractInterface<CR>

nnoremap <leader>fz <Cmd>Vsc Edit.GoToText<CR>

" Git (LazyVim-style).
nnoremap <leader>gg <Cmd>Vsc Team.Git.GoToGitChanges<CR>
nnoremap <leader>gc <Cmd>Vsc Team.Git.Commit<CR>
nnoremap <leader>gp <Cmd>Vsc Team.Git.Pull<CR>
nnoremap <leader>gP <Cmd>Vsc Team.Git.Push<CR>
nnoremap <leader>gb <Cmd>Vsc Team.Git.ManageBranches<CR>
nnoremap <leader>gl <Cmd>Vsc Team.Git.ViewHistory<CR>
nnoremap <leader>gd <Cmd>Vsc Diff.CompareWithUnmodified<CR>
nnoremap <leader>gB <Cmd>Vsc Team.Git.Annotate<CR>
nnoremap <leader>vc <Cmd>Vsc Team.Git.GoToGitChanges<CR>
nnoremap <leader>gr <Cmd>Vsc View.GitRepositoryWindow<CR>

" UI toggles.
nnoremap <leader>uf <Cmd>Vsc View.FullScreen<CR>
nnoremap <leader>uw <Cmd>Vsc Edit.ToggleWordWrap<CR>

" Buffer navigation, LazyVim style. <S-h> is just H, which TextInput delivers.
nnoremap <S-h> <Cmd>Vsc Window.PreviousTab<CR>
nnoremap <S-l> <Cmd>Vsc Window.NextTab<CR>
nnoremap <leader>bb <Cmd>Vsc Debug.ToggleBreakpoint<CR>
nnoremap <leader>bd <Cmd>Vsc Window.CloseDocumentWindow<CR>
nnoremap <leader>bo <Cmd>Vsc Window.CloseAllButThis<CR>

" Splits, LazyVim style.
nnoremap <leader>- <Cmd>Vsc Window.NewHorizontalTabGroup<CR>
nnoremap <leader><Bar> <Cmd>Vsc Window.NewVerticalTabGroup<CR>

" LazyVim LSP-style.
nnoremap <leader>cr <Cmd>Vsc Refactor.Rename<CR>
nnoremap <leader>ca <Cmd>Vsc View.QuickActions<CR>
vnoremap <leader>ca <Cmd>Vsc View.QuickActions<CR>
vnoremap <leader>cf <Cmd>Vsc Edit.FormatSelection<CR>

" Telescope-like.
nnoremap <leader><space> <Cmd>Vsc Edit.GoToFile<CR>
nnoremap <leader>/ <Cmd>Vsc Edit.FindInFiles<CR>
nnoremap <leader>: <Cmd>Vsc View.CommandWindow<CR>
nnoremap <leader>fb <Cmd>Vsc Window.Windows<CR>
nnoremap <leader>fr <Cmd>Vsc Edit.GoToRecentFile<CR>
" <leader>fw, not <leader>sw: that is a prefix of the <leader>sw) surround
" mappings above, and a prefix waits out 'timeoutlen' before it fires.
nnoremap <leader>fw <Cmd>Vsc Edit.FindInFiles<CR>
nnoremap gs <Cmd>Vsc Edit.GoToSymbol<CR>

nnoremap <leader>ci <Cmd>Vsc Edit.ListMembers<CR>
nnoremap <leader>cs <Cmd>Vsc Edit.CompleteWord<CR>

" Call/type hierarchy, navigation bar, document outline.
nnoremap <leader>ch <Cmd>Vsc Edit.ViewCallHierarchy<CR>
nnoremap <leader>th <Cmd>Vsc View.ClassView<CR>
nnoremap <leader>tt <Cmd>Vsc Edit.ViewTypeHierarchy<CR>
nnoremap <leader>vn <Cmd>Vsc Window.MoveToNavigationBar<CR>
nnoremap <leader>do <Cmd>Vsc View.DocumentOutline<CR>

" Collapse/expand regions.
nnoremap zC <Cmd>Vsc Edit.CollapseAllOutlining<CR>
nnoremap zO <Cmd>Vsc Edit.ExpandAllOutlining<CR>
nnoremap zT <Cmd>Vsc Edit.ToggleAllOutlining<CR>

" Go to enclosing brace.
nnoremap [{ <Cmd>Vsc Edit.GotoBrace<CR>
nnoremap ]} <Cmd>Vsc Edit.GotoBrace<CR>
vnoremap [{ <Cmd>Vsc Edit.GotoBraceExtend<CR>
vnoremap ]} <Cmd>Vsc Edit.GotoBraceExtend<CR>

" CamelCase word-part navigation.
nnoremap <A-b> <Cmd>Vsc Edit.WordPrevious<CR>
nnoremap <A-w> <Cmd>Vsc Edit.WordNext<CR>

" Last edit location.
nnoremap ge <Cmd>Vsc Edit.GoToLastEditLocation<CR>

" Multi-caret: the <C-A-,> family goes straight to Visual Studio
" (Ctrl+Alt is never claimed by VsNeo). Shift+Alt chords pass through too,
" so VS's own multi-caret works natively: Shift+Alt+. adds the next
" matching caret, Shift+Alt+; selects all matches - add a few carets,
" press i, and type; every caret edits at once. Select-all-matching is
" also here as a mapping:
vnoremap <leader>sa <Cmd>Vsc Edit.InsertCaretsatAllMatching<CR>

" Multi-edit: arm the matches of the last search (/ or *), change one with
" cgn/ciw, and Esc replays the change at every other match.
nnoremap <leader>mm <Cmd>lua vsneo.multi_edit()<CR>

" Clipboard ring. The insert-mode chord is omitted - VS's own Ctrl+Shift+V
" works natively in insert mode.
nnoremap <leader>pr <Cmd>Vsc Edit.CycleClipboardRing<CR>

" Duplicate line/selection.
nnoremap <leader>yd <Cmd>Vsc Edit.Duplicate<CR>
vnoremap <leader>yd <Cmd>Vsc Edit.Duplicate<CR>

" Task list and TODO navigation.
nnoremap <leader>tl <Cmd>Vsc View.TaskList<CR>
nnoremap ]t <Cmd>Vsc Edit.NextTask<CR>
nnoremap [t <Cmd>Vsc Edit.PreviousTask<CR>

" NuGet, new item/class, references.
nnoremap <leader>ng <Cmd>Vsc Tools.ManageNuGetPackagesforSolution<CR>
nnoremap <leader>na <Cmd>Vsc Project.AddNewItem<CR>
nnoremap <leader>nc <Cmd>Vsc Project.AddClass<CR>
nnoremap <leader>nr <Cmd>Vsc Project.AddReference<CR>

nnoremap <leader>vib <Cmd>Vsc Edit.SelectContainingDeclaration<CR>

" Indent with BS/TAB in normal/visual mode.
nnoremap <BS> <<
nnoremap <TAB> >>
xnoremap <BS> <gv
xnoremap <TAB> >gv

" Argument splitting.
vnoremap <leader>l, :s/, /,\r/g<CR>gv=:noh<CR>
nnoremap <leader>lis vi(:s/, /,\r/g<CR>:Vsc Edit.FormatSelection<CR>:noh<CR>
nnoremap <leader>fas f(a<CR><Esc>:s/, /, \r/g<CR>:noh<CR>

" Debugger, tests and Git: nvim-dap / neotest / gitsigns-style keys driving
" Visual Studio. The Lua rc (examples/vsneorc.lua) has the same with
" which-key descriptions.
nnoremap <leader>db <Cmd>Vsc Debug.ToggleBreakpoint<CR>
nnoremap <leader>dc <Cmd>Vsc Debug.Start<CR>
nnoremap <leader>dC <Cmd>Vsc Debug.RunToCursor<CR>
nnoremap <leader>dn <Cmd>Vsc Debug.StartWithoutDebugging<CR>
nnoremap <leader>di <Cmd>Vsc Debug.StepInto<CR>
nnoremap <leader>do <Cmd>Vsc Debug.StepOut<CR>
nnoremap <leader>dO <Cmd>Vsc Debug.StepOver<CR>
nnoremap <leader>dr <Cmd>Vsc Debug.Restart<CR>
nnoremap <leader>dt <Cmd>Vsc Debug.StopDebugging<CR>
nnoremap <leader>dp <Cmd>Vsc Debug.BreakAll<CR>
nnoremap <leader>dq <Cmd>Vsc Debug.QuickWatch<CR>
nnoremap <leader>dk <Cmd>Vsc Debug.CallStack<CR>
nnoremap <leader>tt <Cmd>Vsc TestExplorer.RunAllTestsInContext<CR>
nnoremap <leader>td <Cmd>Vsc TestExplorer.DebugAllTestsInContext<CR>
nnoremap <leader>tT <Cmd>Vsc TestExplorer.RunAllTests<CR>
nnoremap <leader>tl <Cmd>Vsc TestExplorer.RepeatLastRun<CR>
nnoremap <leader>te <Cmd>Vsc TestExplorer.ShowTestExplorer<CR>
nnoremap <leader>gg <Cmd>Vsc Team.Git.GoToGitChanges<CR>
nnoremap <leader>gc <Cmd>Vsc Team.Git.CommitOrStash<CR>
nnoremap <leader>gp <Cmd>Vsc Team.Git.Push<CR>
nnoremap <leader>gP <Cmd>Vsc Team.Git.Pull<CR>
nnoremap <leader>gh <Cmd>Vsc Team.Git.ViewHistory<CR>
nnoremap <leader>gB <Cmd>Vsc Team.Git.ManageBranches<CR>
