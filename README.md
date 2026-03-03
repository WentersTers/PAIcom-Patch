Patcher

Extends your own copy of PAIcom.exe with a file‑watching runtime, enabling you to trigger in‑game commands by writing a phrase to a text file — no microphone required.

Legal notice: This tool contains no game code. It reads your locally‑installed copy of PAIcom.exe, and creates an interoperable executable based on your local installation. You must own a legitimate copy of PAIcom (e.g. via Steam) to use this tool.
This software is provided for research and interoperability purposes.

---

Requirements

Requirement Notes
Windows x64 The patcher is self‑contained — no .NET install needed
.NET Framework 4.8 Required by PAIcom itself; already present if you can launch the game
PAIcom.exe Your own Steam copy — not included

---

Quick start

1. Copy PAIcomPatcher.exe into the same folder as PAIcom.exe
      (usually SteamLibrary\steamapps\common\PAIcom).
2. Open a terminal in that folder and run:
   ```
   PAIcomPatcher.exe PAIcom.exe --out PAIcom.patched.exe --backup
   ```
3. Launch PAIcom.patched.exe instead of the original.
      (You can rename it back to PAIcom.exe and keep the original as PAIcom.original.exe for easy troubleshooting.)
4. A file called command_input.txt is created next to the exe on first run.
      Write any recognised phrase to it (one line, then save) to dispatch the matching in‑game command:
   ```
   hey paicom open youtube
   ```
5. Watch hotswap.log in the same directory for diagnostic output.

---

Command mapping

Place a file at custom-commands\commands.txt (relative to the exe) with one command per line in this format:

```
hey paicom open youtube (youtube.txt)
hey paicom open google (google.txt)
hey paicom show animations (animations.txt)
```

The part in parentheses is the name of the matching animation script inside the animations\ folder (.txt extension is stripped automatically). Matching is case‑insensitive.

---

Animation scripts

Each file in animations\ can use these directives (one per line):

```
HIDE_ALL
SHOW 3
HIDE 1
OPEN_URL https://example.com
PLAY_AUDIO audio/chime.wav
WAIT 500
```

Directive Description
HIDE_ALL Hides every PictureBox on the form
SHOW N Shows PictureBox at index N
HIDE N Hides PictureBox at index N
OPEN_URL <url> Opens the URL in the default browser
PLAY_AUDIO <path> Plays a .wav file (relative to the exe folder)
WAIT <ms> Pauses script execution for N milliseconds

SHOW/HIDE/HIDE_ALL directly manipulate the game's own PictureBox controls, discovered at runtime.

Animation scripts are used when the speech engine dispatch chain fails (see below). When [EMULATE-ASYNC] OK or [EMULATE-STOP] OK appears in the log, the game's own full handler ran and played its built‑in animations.

---

Dry run / diagnostics

```
PAIcomPatcher.exe PAIcom.exe --dry-run --verbose
```

Prints what would be patched without writing any files.

---

Building from source

Requires the .NET 8 SDK.

```
git clone https://github.com/WentersTers/PAIcom-Patch-Injector.git
cd PAIcom-Patch-Injector
dotnet build PAIcomPatcher.csproj -c Release
```

How to find the PAIcom folder path

1. Open Steam and go to your Library.
2. Click PAIcom.
3. Click the gear icon next to the Play button.
4. Choose Manage → Browse local files.
5. Copy the folder path from Explorer's address bar.
6. Use that path in the cd command below.

Compiling a self‑contained release exe (PowerShell)

```powershell
cd 'C:\path\to\PAIcom'   # paste your PAIcom folder path here

dotnet publish PAIcomPatcher.csproj -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true `
    -p:PublishTrimmed=false -o publish\
```

This bundles .NET 8 into the output so end‑users do not need a separate runtime installed. The finished PAIcomPatcher.exe is written to the publish\ sub‑folder.

Then:

1. Copy publish\PAIcomPatcher.exe into the same folder as PAIcom.exe.
2. Run it as described in Quick start.

---

How it works (technical summary)

1 — Patch‑time: method location

The patcher scans the methods in the target assembly to locate:

· The main command dispatcher — the function that processes recognised speech.
· The main constructor — used as the extension entry point.

Candidate methods are identified by examining their instruction count and branching characteristics.

2 — Patch‑time: runtime compilation

A C# HotSwapRuntime class (Core/HotSwapTemplate.cs) is compiled against .NET Framework 4.8 reference assemblies and added as a new type directly into the target module. No external DLLs are written to disk.

3 — Patch‑time: IL extension

Two points are modified in the patched assembly:

Location Added call
Main constructor HotSwapRuntime.StartWatcher() after the base constructor
Command dispatcher HotSwapRuntime.OnCommandDispatched(command) before every return

4 — Runtime: start‑up

When PAIcom.patched.exe starts, StartWatcher() runs and:

· Creates command_input.txt if it does not exist.
· Starts a FileSystemWatcher on command_input.txt.
· Starts a 500 ms polling loop as a write‑lock fallback.
· Spawns a background thread that waits 3 seconds then begins searching for the SpeechRecognitionEngine instance.

5 — Runtime: engine discovery

The background thread walks every open Form via Application.OpenForms, reading instance fields to find a SpeechRecognitionEngine or SpeechRecognizer. On success it logs:

```
[ENGINE] Ready: EmulateRecognize, EmulateRecognizeAsync, RecognizeAsyncCancel, RecognizeAsync
```

At the same time the form is scanned for:

· PictureBox controls (for script fallback).
· Animation method — any instance method with signature Task Method(string).
· Command handler method — a fallback void Method(string) if present.

6 — Runtime: command dispatch (5‑attempt chain)

When a phrase is written to command_input.txt the runtime:

1. Reads and trims the file content.
2. Looks up the phrase in custom-commands/commands.txt to resolve a command name.
3. Runs the following dispatch chain in order, stopping at the first success:

Attempt Strategy Log prefix
1 EmulateRecognizeAsync(phrase) on the UI thread — marshalled via Form.Invoke(). Fires the game's own full handler with animations, audio, and URLs. [EMULATE-ASYNC]
2 Stop/restart — calls RecognizeAsyncCancel(), then EmulateRecognize(phrase), then RecognizeAsync(Multiple), all on the UI thread. [EMULATE-STOP]
3 Direct animation method — calls the discovered Task Method(string) on the UI thread via BeginInvoke. [DIRECT-INVOKE]
4 Command handler method — calls the discovered void Method(string) on the UI thread via BeginInvoke. [CMD-HANDLER]
5 Script fallback — reads and executes the matching animations/*.txt file locally, controlling PictureBoxes via reflection. [SCRIPT]

Important: SpeechRecognitionEngine is thread‑affine — all calls to it are marshalled through Form.Invoke() to ensure they run on the correct UI thread.

---

Reading the log

hotswap.log (written next to the exe) records every step. A successful dispatch looks like:

```
[21:34:02.596] [DISPATCH] 'aliexpress' (phrase: 'hey paicom open aliexpress')
[21:34:02.596] [DISPATCH] Thread: HotSwapPoller (ID 6, IsBackground=True)
[21:34:02.596] [DISPATCH] State: engine=True emulate=True emulateAsync=True animMethod=False mainForm=True
[21:34:02.599] [EMULATE-ASYNC] Marshalling EmulateRecognizeAsync("hey paicom open aliexpress") to UI thread …
[21:34:02.601] [EMULATE-ASYNC] OK – game handler will fire asynchronously.
```

If all engine attempts fail the script fallback runs:

```
[21:34:03.015] [SCRIPT] Running E:\...\animations\aliexpress.txt
[21:34:03.022] [OPEN_URL] https://www.aliexpress.com/
[21:34:03.045] [PLAY_AUDIO] E:\...\audio\aliexpress.wav
```

---

License

MIT — see LICENSE

This tool does not circumvent copy protection, licensing, or DRM.
It operates solely on a local copy of the software for interoperability purposes.
Users are responsible for complying with PAIcom's EULA.
