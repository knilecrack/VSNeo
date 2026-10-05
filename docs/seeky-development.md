# Working on the Seeky picker

How to build VSNeo with the Seeky picker in it, and how to take changes you
made in the [Seeky](https://github.com/knilecrack/Seeky) repo into VSNeo. All
commands run in PowerShell from the VSNeo checkout.

## Where the code lives

| Path | What it is |
|---|---|
| `external/Seeky` | A git submodule: the Seeky repo, pinned to one commit. |
| `SeekyEngine/` | `seeky-engine.exe`, the search engine process. It compiles `FffNativeClient`, `SymbolIndex`, `SymbolClassifier`, `FuzzyMatcher` and `SeekyRange`, and ships `fff_c.dll`, straight from `external/Seeky/vs2026/SeekyVS`. |
| `VSNeo.Seeky/` | The picker inside Visual Studio: window, page (`WebUI/index.html`), controller, VSNeo's extra pickers. These are VSNeo's own copies (.NET Framework ports of Seeky's files plus VSNeo additions), not taken from the submodule. |
| `VSNeo_Extension/Seeky/` | The Ctrl+Shift+Alt chords and the bridge to nvim (`VsNeoSeekyHost`). |

## First time

```powershell
git clone --recurse-submodules https://github.com/knilecrack/VSNeo.git
```

In an existing checkout without the submodule (the build stops and tells you
when it is missing):

```powershell
git submodule update --init
```

You need the **.NET 10 SDK** as well as Visual Studio: the build publishes
`seeky-engine.exe` with `dotnet publish`.

## Build and run

| To | Do |
|---|---|
| Try it | Open `VSNeo.slnx` in Visual Studio and press **F5** (experimental instance). |
| Install it into your normal Visual Studio | Close Visual Studio, then `.\install-local.ps1`. It builds the VSIX (the version bumps itself), installs it into every Visual Studio 17.14+ and makes Visual Studio rescan. `-Instance 2022` picks one, `-Configuration Debug` turns on key tracing. |
| Only build | `msbuild VSNeo.slnx -restore -p:Configuration=Release` (Developer PowerShell, where `msbuild` is on PATH). |

Every local build bumps the version in
`VSNeo_Extension/source.extension.vsixmanifest`. Commit it or drop it with
`git restore VSNeo_Extension/source.extension.vsixmanifest`.

After `git pull`, also run `git submodule update --init`: someone may have
moved the submodule.

## Taking a Seeky change into VSNeo

1. In the Seeky repo: commit and push as usual.
2. In VSNeo, move the submodule to Seeky's latest and see what it brings:

   ```powershell
   git submodule update --remote external/Seeky
   git diff --submodule=log
   ```

3. Build or F5, as above.
4. Record the new Seeky commit in VSNeo:

   ```powershell
   git add external/Seeky
   git commit -m "Seeky: bump"
   git push
   ```

`git config diff.submodule log` (once) makes every `git diff` and
`git status` show the Seeky commits a bump brings.

### Before pushing the Seeky change

To try a Seeky change in VSNeo before pushing it, point the submodule at your
local Seeky checkout:

```powershell
git -C external/Seeky fetch O:\repos\knilecrack\Seeky master
git -C external/Seeky checkout FETCH_HEAD
```

Build and test; once it is right, push from Seeky and do step 2. Don't commit
the submodule pointing at a commit that isn't pushed: nobody else (and no CI)
can fetch it.

### What a bump does not bring

The submodule feeds the engine only. If `git diff --submodule=log` lists Seeky
commits that touched the picker itself (`vs2026/SeekyVS/WebUI/index.html`,
`SeekyState.cs`, `SymbolOutline.cs`, `SeekyModalWindowManager.cs`, ...), port
those changes by hand into the matching file in `VSNeo.Seeky/`. To see one:

```powershell
git -C external/Seeky show <commit> -- vs2026/SeekyVS/WebUI/index.html
```

## Where to look when something breaks

- `%TEMP%\vsneo.log`: the extension and the picker (lines start `seeky:`).
- `%TEMP%\vsneo-seeky-engine.log`: the engine process.
- In the picker, **Ctrl+/** lists every key and picker.
