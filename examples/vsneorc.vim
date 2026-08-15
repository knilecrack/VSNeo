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
"   * Insert mode belongs to Visual Studio. nvim never sees insert-mode keys
"     (only <Esc> and <C-w> are claimed), so 'inoremap' entries can never
"     fire. jj/jk to escape, insert-mode line movers and insert-mode
"     clipboard-ring mappings from the .vsvimrc are omitted on purpose. VS's own insert-mode editing
"     (IntelliSense, Copilot Tab-accept, Ctrl+V paste) keeps working natively.
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

" F1 help and VS's C-j/C-k are dead in normal/visual mode. The inoremap
" versions from the .vsvimrc are omitted - insert mode never reaches nvim.
nnoremap <F1> <nop>
vnoremap <F1> <nop>
nnoremap <C-j> <nop>
vnoremap <C-j> <nop>
nnoremap <C-k> <nop>
vnoremap <C-k> <nop>

" :Flash was a VsVim-side plugin command; no equivalent exists in VsNeo.
" nmap <leader>z :Flash<CR>

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
vnoremap <leader>S' c"<C-r>"'<Esc>
vnoremap <leader>S) c(<C-r>")<Esc>
vnoremap <leader>S] c[<C-r>"]<Esc>
vnoremap <leader>S} c{<C-r>"}<Esc>
vnoremap <leader>S> c<lt><C-r>"><Esc>
vnoremap <leader>S* c/*<C-r>"*/<Esc>

" Type a delimiter for splitting the line into separate lines.
nnoremap <Leader><Leader>\ :s//&\r<Left><Left><Left><Left>
xnoremap <Leader><Leader>\ :s//&\r<Left><Left><Left><Left>

nnoremap <Esc> :nohl<CR>

" Reload this file.
nnoremap <leader>vs :source ~/.vsneorc<CR>:echo "vsneorc reloaded"<CR>
map zl :source ~/.vsneorc<CR>:echo "vsneorc reloaded"<CR>

nnoremap <leader>a :Vsc Edit.GoToAll<CR>
nnoremap <leader>y "+y
vnoremap <leader>y "+y

" inoremap jj/jk <ESC> intentionally omitted: insert mode belongs to VS.

" Keep search results centered (last-wins resolution of n/N/*/#).
nnoremap n nzzzv
nnoremap N Nzzzv
nnoremap * *zzzv
nnoremap # #zzzv

nnoremap Y y$

" Replace the visual selection with the default register without yanking it.
vnoremap <leader>p "_dP

" Document window and tab group navigation.
nnoremap <leader>mp :Vsc Window.MoveToPreviousTabGroup<CR>
nnoremap <leader>mx :Vsc Window.MoveToNextTabGroup<CR>

nnoremap <leader>sg :Vsc SeekyVS.SeekyLiveGrepCommand<CR>
nnoremap <leader>vf :Vsc SeekyVS.SeekyLiveGrepCommand<CR>

" Keep scrolling centered.
nnoremap <C-d> <C-d>zzzv
nnoremap <C-u> <C-u>zzzv

" Keep joins centered, cursor on the front.
nnoremap J mzJ`z

" Move lines up/down. Insert-mode versions omitted (insert passes through).
nnoremap <A-]> :m .+1<CR>==
nnoremap <A-[> :m .-2<CR>==
vnoremap <A-Down> :m '>+1<CR>gv=gv
vnoremap <A-Up> :m '<-2<CR>gv=gv

" Easier indent in visual mode: stay in visual mode.
vnoremap < <gv
vnoremap > >gv

" Toggles.
noremap <leader>ln :Vsc Edit.ToggleLineNumbers<CR>
noremap <leader>wws :Vsc Edit.ViewWhiteSpace<CR>

" Line-ending punctuation helpers.
noremap <leader>. :Vsc View.QuickActions<CR>
noremap <leader>, :s/\v\s*(,\s*)*$/,/<CR>:nohl<CR>
noremap <leader>; :s/\v\s*(;\s*)*$/;/<CR>:nohl<CR>
noremap <leader>x :s/.\{1}$//<CR>:nohl<CR>

" Pinning and tab management.
noremap <leader>wp :Vsc Window.PinTab<CR>
noremap <leader>wca :Vsc Window.CloseAllButPinned<CR>
nnoremap <A-.> :Vsc Window.NextTab<CR>
nnoremap <A-,> :Vsc Window.PreviousTab<CR>
nnoremap <leader>c :Vsc Window.Close<CR>

" Window navigation.
nnoremap <leader>wf :Vsc FullScreen<CR>
nnoremap <leader>wc :Vsc Window.CloseDocumentWindow<CR>
nnoremap <leader>wj :Vsc Window.NextDocumentWindow<cr>
nnoremap <leader>wk :Vsc Window.PreviousDocumentWindow<CR>
nnoremap <c-w><c-f> :Vsc FullScreen<CR>
nnoremap <c-w><c-c> :Vsc Window.CloseDocumentWindow<CR>
nnoremap <c-w><c-j> :Vsc Window.NextDocumentWindow<CR>
nnoremap <c-w><c-k> :Vsc Window.PreviousDocumentWindow<CR>

xnoremap <leader>sw :Vsc Edit.SurroundWith<CR>
nnoremap <leader>is :Vsc Edit.InsertSnippet<CR>

" Comment / uncomment.
nnoremap <leader>cc V:Vsc Edit.CommentSelection<CR>
xnoremap <leader>cc :Vsc Edit.CommentSelection<CR>
nnoremap <leader>CC V:Vsc Edit.UncommentSelection<CR>
xnoremap <leader>CC :Vsc Edit.UncommentSelection<CR>

nnoremap <leader>fif :Vsc Edit.FindInFiles<cr>
nnoremap <leader><CR> :nohlsearch<cr>

" K - quick info and parameter details. Overrides VsNeo's built-in K.
nnoremap K :Vsc Edit.QuickInfo<CR>:Vsc Edit.ParameterInfo<CR>

" Improve navigation when wrapping: VS owns wrap, so j/k follow screen lines.
nnoremap j gj
nnoremap k gk

" Navigation history.
noremap <C>- :Vsc View.NavigateBackward<CR>
noremap <C>= :Vsc View.NavigateForward<CR>

" Goto commands.
nnoremap gd :Vsc Edit.GotoDefinition<cr>
nnoremap <leader>d :Vsc Edit.GotoDeclaration<cr>
nnoremap gi :Vsc Edit.GoToImplementation<cr>
nnoremap gf :Vsc Edit.GoToFile<cr>
nnoremap <leader>f :Vsc Edit.GoToFile<cr>
nnoremap gt :Vsc Edit.GoToType<cr>
nnoremap gT :Vsc Edit.GotoTypeDefinition<cr>
nnoremap <leader>rf :Vsc Edit.GoToRecentFile<CR>
nnoremap <leader>sf :Vsc EditorContextMenus.CodeWindow.ToggleHeaderCodeFile<cr>

noremap <leader>ff :Vsc Edit.GoToFile<CR>
noremap <leader>fm :Vsc Edit.GoToMember<CR>
noremap <leader>fw :Vsc Edit.GoToAll<CR>
noremap <leader>gs :Vsc Edit.GoToSymbol<CR>
noremap <leader>gco :Vsc Copilot.ToggleCompletions<CR>
noremap <leader>gcp :Vsc Copilot.ToggleCompletions<CR>

" Refactor.
nnoremap <leader>rn :Vsc Refactor.Rename<cr>
xnoremap <leader>rn :Vsc Refactor.Rename<cr>
xnoremap <leader>rem :Vsc Refactor.ExtractMethod<cr>
nnoremap <leader>rem :Vsc Refactor.ExtractMethod<cr>
xnoremap <leader>rrp :Vsc Refactor.RemoveParameters<cr>
nnoremap <leader>rrp :Vsc Refactor.RemoveParameters<cr>
xnoremap <leader>rop :Vsc Refactor.ReorderParameter<CR>
nnoremap <leader>rop :Vsc Refactor.ReorderParameters<cr>

" Code generation.
nnoremap <leader>gh :Vsc EditorContextMenus.CodeWindow.GenerateEqualsAndGetHashCode<CR>

" Tests.
noremap <leader>tr :Vsc TestExplorer.RunSelectedTests<CR>
noremap <leader>ta :Vsc TestExplorer.RunAllTests<CR>
noremap <leader>tf :Vsc TestExplorer.RunFailedTests<CR>
noremap <leader>td :Vsc TestExplorer.DebugSelectedTests<CR>
noremap <leader>tss :Vsc TestExplorer.ShowTestExplorer<CR>
noremap <leader>tsc :Vsc View.CodeCoverageResults<CR>

" Breakpoints. <leader>bd resolves to CloseDocumentWindow below (last wins).
noremap <leader>be :Vsc Debug.EnableAllBreakpoints<CR>
noremap <leader>br :Vsc Debug.DeleteAllBreakpoints<CR>
noremap <leader>ba :Vsc Debug.Breakpoints<CR>

" Build / debug.
noremap <leader>sb :Vsc Build.BuildSolution<CR>
noremap <leader>sc :Vsc Build.CleanSolution<CR>
noremap <leader>sbs :Vsc Build.BuildSelection<CR>
noremap <leader>scs :Vsc Build.CleanSelection<CR>
noremap <leader>sd :Vsc Debug.Start<CR>
noremap <leader>sr :Vsc Debug.StartWithoutDebugging<CR>
noremap <leader>sbc :Vsc Build.Cancel<CR>
noremap <leader>sdc :Vsc Debug.StopDebugging<CR>
nnoremap <Leader>qw :Vsc Debug.QuickWatch<CR>
nnoremap <C-Left> :Vsc Debug.SetNextStatement<CR>
nnoremap <C-Right> :Vsc Debug.StepOver<CR>
nnoremap <C-Down> :Vsc Debug.StepInto<CR>
nnoremap <C-Up> :Vsc Debug.StepOut<CR>

" Solution explorer / blame.
nnoremap <leader>e :Vsc View.SolutionExplorer<CR>
nnoremap <leader>lb :Vsc Git.Blame<CR>

" PeasyMotion bindings (work if the extension is installed).
noremap ,, :Vsc Tools.InvokePeasyMotion<CR>
noremap <leader>ls :Vsc Tools.InvokePeasyMotionLineJumptoWordBegining<CR>
noremap <leader>le :Vsc Tools.InvokePeasyMotionLineJumpToWordEnding<CR>
noremap ,t :Vsc Tools.InvokePeasyMotionJumpToDocumentTab<CR>
nmap ;l gS:Vsc Tools.InvokePeasyMotionJumpToLineBegining<CR>
nmap ;c gS:Vsc Tools.InvokePeasyMotionTwoCharJump<CR>

" Visual Assist.
noremap <leader>ms :Vsc VAssistX.FindSelected<CR>
noremap <leader>gD :Vsc View.ClassViewShowDerivedTypes<CR>
noremap <C-w>v :Vsc Window.NewVerticalTabGroup<CR>
noremap <leader>np :Vsc OtherContextMenus.UITestEditorContextMenu.Splitintoanewmethod<CR>
noremap <leader>ww :Vsc Window.Windows<CR>

" Quick Actions (lightbulb).
nnoremap <leader>qa :Vsc View.QuickActions<CR>

" Format document / selection.
nnoremap <leader>fd :Vsc Edit.FormatDocument<CR>
vnoremap <leader>fd :Vsc Edit.FormatSelection<CR>

" Organize usings.
nnoremap <leader>ou :Vsc Edit.RemoveAndSort<CR>

" Navigate errors/warnings.
nnoremap ]e :Vsc View.NextError<CR>
nnoremap [e :Vsc View.PreviousError<CR>
nnoremap ]d :Vsc Edit.GoToNextIssueinFile<CR>
nnoremap [d :Vsc Edit.GoToPreviousIssueinFile<CR>

" Navigate methods/members.
nnoremap ]m :Vsc Edit.NextMethod<CR>
nnoremap [m :Vsc Edit.PreviousMethod<CR>

" Peek definition / implementation. <leader>gp resolves to Git Pull below.
nnoremap gp :Vsc Edit.PeekDefinition<CR>
nnoremap gP :Vsc Edit.PeekImplementation<CR>

" Copilot Chat.
nnoremap <leader>ai :Vsc GitHub.Copilot.Chat.ToggleChatWindow<CR>
nnoremap <leader>ae :Vsc Explain<CR>
vnoremap <leader>ae :Vsc Explain<CR>
nnoremap <leader>af :Vsc GitHub.Copilot.Chat.Fix<CR>
vnoremap <leader>af :Vsc GitHub.Copilot.Chat.Fix<CR>
nnoremap <leader>ad :Vsc GitHub.Copilot.Chat.Doc<CR>
vnoremap <leader>ad :Vsc GitHub.Copilot.Chat.Doc<CR>

" Bookmarks.
nnoremap <leader>mt :Vsc Edit.ToggleBookmark<CR>
nnoremap <leader>mn :Vsc Edit.NextBookmark<CR>
nnoremap <leader>mN :Vsc Edit.PreviousBookmark<CR>
nnoremap <leader>mc :Vsc Edit.ClearBookmarks<CR>
nnoremap <leader>ma :Vsc View.BookmarkWindow<CR>

" Sync Solution Explorer with the active document, error list, output.
nnoremap <leader>el :Vsc SolutionExplorer.SyncWithActiveDocument<CR>
nnoremap <leader>er :Vsc View.ErrorList<CR>
nnoremap <leader>to :Vsc View.Output<CR>

" Extract interface.
nnoremap <leader>rei :Vsc Refactor.ExtractInterface<CR>

nnoremap <leader>fz :Vsc Edit.GoToText<CR>

" Git (LazyVim-style).
nnoremap <leader>gg :Vsc Team.Git.GoToGitChanges<CR>
nnoremap <leader>gc :Vsc Team.Git.Commit<CR>
nnoremap <leader>gp :Vsc Team.Git.Pull<CR>
nnoremap <leader>gP :Vsc Team.Git.Push<CR>
nnoremap <leader>gb :Vsc Team.Git.ManageBranches<CR>
nnoremap <leader>gl :Vsc Team.Git.ViewHistory<CR>
nnoremap <leader>gd :Vsc Diff.CompareWithUnmodified<CR>
nnoremap <leader>gB :Vsc Team.Git.Annotate<CR>
nnoremap <leader>vc :Vsc View.GitChanges<CR>
nnoremap <leader>gr :Vsc View.GitRepository<CR>

" UI toggles.
nnoremap <leader>uf :Vsc View.FullScreen<CR>
nnoremap <leader>uw :Vsc Edit.ToggleWordWrap<CR>

" Buffer navigation, LazyVim style. <S-h> is just H, which TextInput delivers.
nnoremap <S-h> :Vsc Window.PreviousTab<CR>
nnoremap <S-l> :Vsc Window.NextTab<CR>
nnoremap <leader>bb :Vsc Debug.ToggleBreakpoint<CR>
nnoremap <leader>bd :Vsc Window.CloseDocumentWindow<CR>
nnoremap <leader>bo :Vsc Window.CloseAllButThis<CR>

" Splits, LazyVim style.
nnoremap <leader>- :Vsc Window.NewHorizontalTabGroup<CR>
nnoremap <leader><Bar> :Vsc Window.NewVerticalTabGroup<CR>

" LazyVim LSP-style.
nnoremap <leader>cr :Vsc Refactor.Rename<CR>
nnoremap <leader>ca :Vsc View.QuickActions<CR>
vnoremap <leader>ca :Vsc View.QuickActions<CR>
vnoremap <leader>cf :Vsc Edit.FormatSelection<CR>

" Telescope-like.
nnoremap <leader><space> :Vsc Edit.GoToFile<CR>
nnoremap <leader>/ :Vsc Edit.FindInFiles<CR>
nnoremap <leader>: :Vsc View.CommandWindow<CR>
nnoremap <leader>fb :Vsc Window.Windows<CR>
nnoremap <leader>fr :Vsc Edit.GoToRecentFile<CR>
nnoremap <leader>sw :Vsc Edit.FindInFiles<CR>
nnoremap gs :Vsc Edit.GoToSymbol<CR>

nnoremap <leader>ci :Vsc Edit.ListMembers<CR>
nnoremap <leader>cs :Vsc Edit.CompleteWord<CR>

" Call/type hierarchy, navigation bar, document outline.
nnoremap <leader>ch :Vsc Edit.ViewCallHierarchy<CR>
nnoremap <leader>th :Vsc View.ClassView<CR>
nnoremap <leader>tt :Vsc Edit.ViewTypeHierarchy<CR>
nnoremap <leader>vn :Vsc Window.MoveToNavigationBar<CR>
nnoremap <leader>do :Vsc View.DocumentOutline<CR>

" Collapse/expand regions.
nnoremap zC :Vsc Edit.CollapseAllOutlining<CR>
nnoremap zO :Vsc Edit.ExpandAllOutlining<CR>
nnoremap zT :Vsc Edit.ToggleAllOutlining<CR>

" Go to enclosing brace.
nnoremap [{ :Vsc Edit.GotoBrace<CR>
nnoremap ]} :Vsc Edit.GotoBrace<CR>
vnoremap [{ :Vsc Edit.GotoBraceExtend<CR>
vnoremap ]} :Vsc Edit.GotoBraceExtend<CR>

" CamelCase word-part navigation.
nnoremap <A-b> :Vsc Edit.WordPrevious<CR>
nnoremap <A-w> :Vsc Edit.WordNext<CR>

" Last edit location.
nnoremap ge :Vsc Edit.GoToLastEditLocation<CR>

" Multi-caret: the <C-A-,> family now goes straight to Visual Studio
" (Ctrl+Alt is never claimed by VsNeo). Bind the commands there instead.
" Select all occurrences still goes through a mapping:
vnoremap <leader>sa :Vsc Edit.InsertCaretsatAllMatching<CR>

" Clipboard ring. The insert-mode chord is omitted - VS's own Ctrl+Shift+V
" works natively in insert mode.
nnoremap <leader>pr :Vsc Edit.CycleClipboardRing<CR>

" Duplicate line/selection.
nnoremap <leader>yd :Vsc Edit.Duplicate<CR>
vnoremap <leader>yd :Vsc Edit.Duplicate<CR>

" Task list and TODO navigation.
nnoremap <leader>tl :Vsc View.TaskList<CR>
nnoremap ]t :Vsc Edit.NextTask<CR>
nnoremap [t :Vsc Edit.PreviousTask<CR>

" NuGet, new item/class, references.
nnoremap <leader>ng :Vsc Tools.ManageNuGetPackagesforSolution<CR>
nnoremap <leader>na :Vsc Project.AddNewItem<CR>
nnoremap <leader>nc :Vsc Project.AddClass<CR>
nnoremap <leader>nr :Vsc Project.AddReference<CR>

nnoremap <leader>vib :Vsc Edit.SelectContainingDeclaration<CR>

" Indent with BS/TAB in normal/visual mode.
nnoremap <BS> <<
nnoremap <TAB> >>
xnoremap <BS> <gv
xnoremap <TAB> >gv

" Argument splitting.
vnoremap <leader>l, :s/, /,\r/g<CR>gv=:noh<CR>
nnoremap <leader>lis vi(:s/, /,\r/g<CR>:Vsc Edit.FormatSelection<CR>:noh<CR>
nnoremap <leader>fas f(a<CR><Esc>:s/, /, \r/g<CR>:noh<CR>
