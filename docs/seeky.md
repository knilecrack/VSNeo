# Seeky in VSNeo: building and taking Seeky changes

The Seeky picker's shared code lives in the Seeky repo
([knilecrack/Seeky](https://github.com/knilecrack/Seeky)). VSNeo builds it
from the `external/Seeky` git submodule. These files come from there:

- the engine's (`seeky-engine.exe`): `FffNativeClient`, `SymbolIndex`,
  `SymbolClassifier`, `FuzzyMatcher`, `SeekyRange`, and `Tools/fff_c.dll`
- the picker's (`VSNeo.Seeky`, built for .NET Framework): `SeekyState`,
  `RecentFiles`, `SymbolClassifier`, `SymbolOutline`, `FuzzyMatcher`,
  `SeekyRange`
- the picker page: `WebUI/index.html`

There are no copies in VSNeo. To change any of these files, change them in
Seeky, never in VSNeo. VSNeo's own picker code (the window, the controller,
the list pickers, the engine client, `LineSearch`) stays in `VSNeo.Seeky/`.

## First time, or after a pull

```powershell
git clone --recurse-submodules https://github.com/knilecrack/VSNeo.git
# an existing checkout, after git pull or git switch:
git submodule update --init
```

If the submodule is missing, the build stops and says to run
`git submodule update --init`.

## Building a new version

| What | How |
|---|---|
| Try it | Open `VSNeo.slnx`, press **F5** (experimental instance) |
| Install into your normal Visual Studio | Close VS, then `.\install-local.ps1` (builds, bumps the version, installs, refreshes VS) |
| Build only | `msbuild VSNeo.slnx -restore -p:Configuration=Release` |

Every local build bumps the version in
`VSNeo_Extension/source.extension.vsixmanifest`, so that file always shows as
modified. Leave it, or run `git restore` on it.

## Taking a change you made in Seeky

1. In the Seeky repo, commit and push to `master` as usual.
2. In VSNeo, move the submodule to Seeky's latest and see what came in:
   ```powershell
   git submodule update --remote external/Seeky
   git diff --submodule=log
   ```
3. Build or F5, as above.
4. Record the new Seeky version in VSNeo:
   ```powershell
   git add external/Seeky
   git commit -m "Seeky: bump"
   git push
   ```

Once this is on master, Dependabot (`.github/dependabot.yml`) opens steps 2
and 4 as a "Seeky" PR by itself whenever Seeky's master moves. Then you only
test and merge it.

## Trying a Seeky change before pushing it

Point the submodule at your local Seeky checkout, then build:

```powershell
git -C external/Seeky fetch O:\repos\knilecrack\Seeky master
git -C external/Seeky checkout FETCH_HEAD
```

Or edit inside `external/Seeky` directly. It is an ordinary Seeky clone, so
you can branch, commit and push from there. When you're happy, push from
Seeky and do step 2 above.

## Rules for shared files

- **They must build for both targets.** Standalone SeekyVS is .NET 10, and
  VSNeo builds the same files for .NET Framework 4.7.2. Wrap .NET 10-only
  code in `#if NETFRAMEWORK` / `#else`. The usual cases are
  `[GeneratedRegex]`, `System.Threading.Lock` and `Math.Clamp`. Spell out
  ranges and `^` indices (`Substring`, `list[list.Count - 1]`).
- **Page changes must be additive.** The page serves both hosts.
  VSNeo-only behaviour switches on from what the host sends: the Ctrl+V/X/Q
  keys and the help rows need `setState` with `vsneo: true`, the `nvim` theme
  needs a `palette`, and prompt normal mode needs `promptNormal`. Standalone
  SeekyVS never sends these, so it keeps Ctrl+V as paste.
- **Key help:** F1 or Ctrl+/ in the picker. When you add or change a key,
  update the `HELP` table in the page; rows tagged `vsneo` show only in VSNeo.
