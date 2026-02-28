# PAIcom HotSwap Patcher

Injects a file-watching hot-swap runtime into **your own copy** of PAIcom.exe
so you can trigger in-game commands by writing a phrase to a text file — no
microphone required.

> **Legal notice**: This tool contains no game code.  It reads your locally-
> installed copy of PAIcom.exe, patches it in memory, and writes a new file.
> You must own a legitimate copy of PAIcom (e.g. via Steam) to use this tool.

---

## Requirements

| Requirement | Notes |
|-------------|-------|
| Windows x64 | The patcher is self-contained — no .NET install needed |
| .NET Framework 4.8 | Required by PAIcom itself; already present if you can launch the game |
| PAIcom.exe | Your own Steam copy — **not included** |

---

## Quick start

1. Copy `PAIcomPatcher.exe` into the same folder as `PAIcom.exe`
   (usually `SteamLibrary\steamapps\common\PAIcom`).

2. Open a terminal in that folder and run:

   ```
   PAIcomPatcher.exe PAIcom.exe --out PAIcom.patched.exe --backup
   ```

3. Launch `PAIcom.patched.exe` instead of the original.

4. A file called `command_input.txt` is created next to the exe on first run.
   Write any recognised phrase to it (one line, then save) to dispatch the
   matching in-game command:

   ```
   hey paicom open youtube
   ```

5. Watch `hotswap.log` in the same directory for diagnostic output.

---

## Command mapping

Place a file at `custom-commands\commands.txt` (relative to the exe) with one
command per line in this format:

```
hey paicom open youtube (youtube.txt)
hey paicom open google (google.txt)
hey paicom show animations (animations.txt)
```

The part in parentheses is the name of the matching animation script inside
the `animations\` folder (`.txt` extension is stripped automatically).
Matching is case-insensitive.

---

## Animation scripts

Each file in `animations\` can use these directives (one per line):

```
HIDE_ALL
SHOW 3
HIDE 1
OPEN_URL https://example.com
PLAY_AUDIO sounds/chime.wav
WAIT 500
RUN scripts/dostuff.bat
```

These are only used as a fallback.  When the speech engine is discovered at
runtime (logged as `[ENGINE] Ready: …`) the original phrase is fed directly
into the game's own speech handler via `EmulateRecognize`, so all built-in
animations and audio play normally.

---

## Dry run / diagnostics

```
PAIcomPatcher.exe PAIcom.exe --dry-run --verbose
```

Prints what *would* be patched without writing any files.

```
PAIcomPatcher.exe PAIcom.exe --analyze
```

Dumps the top-ranked methods by instruction count, branch density, and call
count — useful for debugging ConfuserEx-obfuscated assemblies.

---

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```
git clone https://github.com/WentersTers/PAIcom-Patch-Injector.git
cd paicom-hotswap-patcher
dotnet build PAIcomPatcher.csproj -c Release
```

To produce a single self-contained exe:

```
dotnet publish PAIcomPatcher.csproj -c Release -r win-x64 ^
  --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false ^
  -o publish\
```

---

## How it works (technical summary)

1. **Structural IL analysis** — because PAIcom uses ConfuserEx (all strings
   encrypted), the patcher locates the `SpeechRecognizedEventArgs` handler and
   the main constructor using branch-density / call-count scoring instead of
   string pattern matching.

2. **Roslyn in-memory compilation** — a small C# 7.3 `HotSwapRuntime` class is
   compiled against .NET Framework 4.8 reference assemblies and injected as a
   new type in the module.

3. **IL injection** — `HotSwapRuntime.StartWatcher()` is called from the main
   constructor (after `base..ctor()`), and `OnCommandDispatched` is appended
   before every `ret` in the speech handler.

4. **Runtime dispatch** — after a 3-second delay, the runtime scans
   `Application.OpenForms` via reflection to find the
   `SpeechRecognitionEngine` instance and resolves `EmulateRecognize(string)`.
   Subsequent dispatches call `EmulateRecognize(originalPhrase)` so the
   game's full built-in handler runs.

---

## License

MIT — see [LICENSE](LICENSE)

