namespace PAIcomPatcher.Core;

/// <summary>
/// The C# source that gets compiled by Roslyn at patch-time and then
/// grafted into the target assembly as a new type.
///
/// This is kept as a plain string so it can be edited without needing to
/// rebuild the patcher itself – you can also externalise it to a .cs file
/// on disk if you prefer to edit it with IDE tooling.
///
/// The compiled type will be inserted into the target assembly and called
/// from the patched init method (via HotSwap.StartWatcher).
/// </summary>
public static class HotSwapTemplate
{
    /// <summary>
    /// Modify this source to implement your actual hot-swap behaviour.
    /// It is compiled against the host's .NET runtime references at patch
    /// time, so you have access to the full BCL.
    /// </summary>
    public const string SourceCode = """
        using System;
        using System.IO;
        using System.Threading;

        /// <summary>
        /// Hot-swap runtime injected into PAIcom.exe.
        ///
        /// Responsibilities:
        ///   1. Watch command_input.txt for changes.
        ///   2. Read the written command token.
        ///   3. Look it up in commands.txt (format: TOKEN=GAME_COMMAND).
        ///   4. Route the resolved command to the game's own dispatch logic.
        ///   5. Log commands that were dispatched (for debugging).
        /// </summary>
        public static class HotSwapRuntime
        {
            // ── Configuration ─────────────────────────────────────────────
            // Use the directory that contains the running exe so paths are
            // correct regardless of the process working directory at startup.
            private static readonly string BaseDir =
                System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location)
                ?? AppDomain.CurrentDomain.BaseDirectory;

            private static string InputFile    => System.IO.Path.Combine(BaseDir, "command_input.txt");
            private static string CommandsFile => System.IO.Path.Combine(BaseDir, "custom-commands", "commands.txt");
            private static string LogFile      => System.IO.Path.Combine(BaseDir, "hotswap.log");

            // ── State ──────────────────────────────────────────────────────
            private static FileSystemWatcher _watcher;
            private static int _started = 0;
            private static Action<string> _dispatchDelegate;
            private static DateTime _lastRead = DateTime.MinValue;
            private static readonly object _lock = new object();

            // Reflection-located speech engine (found after game finishes init)
            private static object _speechEngine;   // SpeechRecognitionEngine or SpeechRecognizer
            private static System.Reflection.MethodInfo _emulateMethod;

            // ── Entry point ────────────────────────────────────────────────

            /// <summary>
            /// Called once from the patched init method.
            /// Idempotent – calls after the first are ignored.
            /// </summary>
            public static void StartWatcher()
            {
                if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
                    return;

                try
                {
                    Log("HotSwapRuntime.StartWatcher() initialising …");
                    EnsureInputFile();
                    StartFSW();
                    StartPollingFallback();

                    // Give the game time to finish its own init, then locate
                    // the SpeechRecognitionEngine/SpeechRecognizer via reflection.
                    var t = new System.Threading.Thread(DelayedEngineSearch)
                    {
                        IsBackground = true,
                        Name = "HotSwapEngineSearch"
                    };
                    t.Start();

                    Log("FileSystemWatcher active.  Write a phrase to command_input.txt to dispatch.");
                }
                catch (Exception ex)
                {
                    Log("[ERROR] StartWatcher failed: " + ex);
                }
            }

            /// <summary>
            /// Register the game's command-dispatch delegate so we can call it.
            /// The patcher can optionally wire this up; if not set, we fall
            /// back to a no-op and log the unresolved command.
            /// </summary>
            public static void RegisterDispatchDelegate(Action<string> dispatch)
            {
                _dispatchDelegate = dispatch;
            }

            /// <summary>
            /// Called by the patched command handler on every dispatch so we
            /// can log command activity.
            /// </summary>
            public static void OnCommandDispatched(string command)
            {
                if (command != null)
                    Log("[CMD] " + command);
            }

            // ── FSW watcher ───────────────────────────────────────────────

            private static void StartFSW()
            {
                var dir  = Path.GetFullPath(".");
                _watcher = new FileSystemWatcher(dir, InputFile)
                {
                    NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += OnInputFileChanged;
                _watcher.Error   += (_, e) => Log($"[FSW error] {e.GetException()}");
            }

            private static void OnInputFileChanged(object src, FileSystemEventArgs e)
            {
                ProcessInputFile();
            }

            // ── Polling fallback (for write-lock race) ────────────────────

            private static void StartPollingFallback()
            {
                var t = new Thread(PollLoop)
                {
                    IsBackground = true,
                    Name         = "HotSwapPoller",
                };
                t.Start();
            }

            private static void PollLoop()
            {
                while (true)
                {
                    Thread.Sleep(500);
                    try
                    {
                        if (!File.Exists(InputFile)) continue;
                        var wt = File.GetLastWriteTimeUtc(InputFile);
                        if (wt > _lastRead)
                            ProcessInputFile();
                    }
                    catch { /* ignore polling transients */ }
                }
            }

            // ── Core processing ───────────────────────────────────────────

            private static void ProcessInputFile()
            {
                lock (_lock)
                {
                    try
                    {
                        var wt = File.GetLastWriteTimeUtc(InputFile);
                        if (wt <= _lastRead) return; // already handled
                        _lastRead = wt;

                        // Retry loop to handle write-lock
                        string token = "";
                        for (int attempt = 0; attempt < 5; attempt++)
                        {
                            try
                            {
                                token = File.ReadAllText(InputFile).Trim();
                                break;
                            }
                            catch (IOException)
                            {
                                Thread.Sleep(50);
                            }
                        }

                        if (token == null || token.Trim() == string.Empty) return;

                        Log($"[INPUT] token='{token}'");

                        var gameCmd = LookupCommand(token);
                        if (gameCmd == null)
                        {
                            Log("[WARN] No mapping found for token '" + token + "' in " + CommandsFile);
                            return;
                        }

                        DispatchCommand(token, gameCmd);
                    }
                    catch (Exception ex)
                    {
                        Log($"[ERROR] ProcessInputFile: {ex}");
                    }
                }
            }

            // ── Command lookup ────────────────────────────────────────────

            /// <summary>
            /// Reads custom-commands/commands.txt, which has lines of the form:
            ///   hey paicom open youtube (youtube.txt)
            ///
            /// Strips the trailing (*.txt) reference, compares the phrase to the
            /// input token, and returns the referenced filename (without .txt)
            /// as the resolved command name.
            /// Returns null if no match.
            /// </summary>
            private static string LookupCommand(string token)
            {
                if (!File.Exists(CommandsFile)) return null;

                var normalizedToken = token.Trim().ToLowerInvariant();

                foreach (var rawLine in File.ReadAllLines(CommandsFile))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    // Extract the (file.txt) reference at the end of the line
                    // Format: "hey paicom do something (file.txt)"
                    var parenOpen  = line.LastIndexOf('(');
                    var parenClose = line.LastIndexOf(')');

                    string phrase;
                    string fileRef;

                    if (parenOpen > 0 && parenClose > parenOpen)
                    {
                        phrase  = line.Substring(0, parenOpen).Trim();
                        fileRef = line.Substring(parenOpen + 1, parenClose - parenOpen - 1).Trim();
                        // Strip .txt extension to get the plain command name
                        if (fileRef.ToLowerInvariant().EndsWith(".txt"))
                            fileRef = fileRef.Substring(0, fileRef.Length - 4);
                    }
                    else
                    {
                        // No parenthesised reference – treat the whole line as phrase
                        phrase  = line;
                        fileRef = line;
                    }

                    if (string.Equals(phrase, normalizedToken, StringComparison.OrdinalIgnoreCase))
                        return fileRef;
                }
                return null;
            }

            // ── Dispatch ──────────────────────────────────────────────────

            /// <summary>
            /// Primary dispatch: try EmulateRecognize so the game's own full
            /// handler fires (animations + audio + URLs).  Falls back to the
            /// local script-runner if the engine has not been found yet.
            /// </summary>
            /// <param name="originalPhrase">Exact phrase as written to command_input.txt</param>
            /// <param name="commandName">Resolved command name (used for script fallback)</param>
            private static void DispatchCommand(string originalPhrase, string commandName)
            {
                Log("[DISPATCH] '" + commandName + "' (phrase: '" + originalPhrase + "')");

                // -- Attempt 1: feed the phrase back into the speech engine --
                if (_emulateMethod != null && _speechEngine != null)
                {
                    try
                    {
                        Log("[EMULATE] Calling EmulateRecognize(\"" + originalPhrase + "\")");
                        _emulateMethod.Invoke(_speechEngine, new object[] { originalPhrase });
                        Log("[EMULATE] OK");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log("[EMULATE] Failed (" + ex.Message + "), falling back to script.");
                    }
                }
                else
                {
                    Log("[EMULATE] Engine not found yet; using script fallback.");
                }

                // -- Attempt 2: run the local animation script --
                var scriptPath = System.IO.Path.Combine(BaseDir, "animations", commandName + ".txt");
                if (!File.Exists(scriptPath))
                {
                    Log("[WARN] Animation script not found: " + scriptPath);
                    return;
                }

                Log("[SCRIPT] Running " + scriptPath);
                RunAnimationScript(scriptPath);
            }

            // ── Speech-engine discovery ───────────────────────────────────

            /// <summary>
            /// Called on a background thread ~3 s after startup.
            /// Walks all open Forms and their fields looking for a
            /// SpeechRecognitionEngine or SpeechRecognizer instance.
            /// Stores the engine + its EmulateRecognize(string) method for later use.
            /// </summary>
            private static void DelayedEngineSearch()
            {
                System.Threading.Thread.Sleep(3000);
                Log("[ENGINE] Starting speech-engine discovery …");
                try
                {
                    FindSpeechEngine();
                }
                catch (Exception ex)
                {
                    Log("[ENGINE] Discovery error: " + ex.Message);
                }
            }

            private static void FindSpeechEngine()
            {
                // Candidate type names (the obfuscated exe still references the real assembly names)
                var engineTypeNames = new string[]
                {
                    "System.Speech.Recognition.SpeechRecognitionEngine",
                    "System.Speech.Recognition.SpeechRecognizer"
                };

                // Walk every open form and its instance fields (recursively one level)
                System.Windows.Forms.Form[] forms = null;
                try
                {
                    var fc = System.Windows.Forms.Application.OpenForms;
                    forms = new System.Windows.Forms.Form[fc.Count];
                    for (int i = 0; i < fc.Count; i++)
                        forms[i] = fc[i];
                }
                catch (Exception ex)
                {
                    Log("[ENGINE] Cannot enumerate OpenForms: " + ex.Message);
                    return;
                }

                foreach (var form in forms)
                {
                    if (form == null) continue;
                    var found = SearchObjectForEngine(form, engineTypeNames, depth: 0, maxDepth: 3);
                    if (found != null)
                    {
                        StoreEngine(found);
                        return;
                    }
                }

                Log("[ENGINE] Speech engine not found in open forms.");
            }

            private static object SearchObjectForEngine(object obj, string[] typeNames, int depth, int maxDepth)
            {
                if (obj == null || depth > maxDepth) return null;

                var type = obj.GetType();

                // Check if this object IS an engine
                foreach (var name in typeNames)
                    if (type.FullName == name)
                        return obj;

                // Scan its instance fields
                var flags = System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Public;

                foreach (var field in type.GetFields(flags))
                {
                    try
                    {
                        var value = field.GetValue(obj);
                        if (value == null) continue;

                        var fieldTypeName = value.GetType().FullName;
                        foreach (var name in typeNames)
                        {
                            if (fieldTypeName == name)
                            {
                                Log("[ENGINE] Found " + fieldTypeName + " in field '" + field.Name + "' of " + type.FullName);
                                return value;
                            }
                        }

                        // Recurse into reference types that are likely to hold the engine
                        if (depth < maxDepth && !value.GetType().IsPrimitive && value.GetType() != typeof(string))
                        {
                            var nested = SearchObjectForEngine(value, typeNames, depth + 1, maxDepth);
                            if (nested != null) return nested;
                        }
                    }
                    catch { /* skip inaccessible fields */ }
                }

                return null;
            }

            private static void StoreEngine(object engine)
            {
                _speechEngine = engine;

                // Resolve EmulateRecognize(string) — present on both engine types
                var method = engine.GetType().GetMethod(
                    "EmulateRecognize",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public,
                    null,
                    new System.Type[] { typeof(string) },
                    null);

                if (method != null)
                {
                    _emulateMethod = method;
                    Log("[ENGINE] Ready: " + engine.GetType().FullName + ".EmulateRecognize(string)");
                }
                else
                {
                    Log("[ENGINE] Engine found but EmulateRecognize(string) not available on " + engine.GetType().FullName);
                }
            }

            /// <summary>
            /// Executes an animation script file.
            /// Supported directives:
            ///   OPEN_URL &lt;url&gt;         - opens URL in default browser
            ///   PLAY_AUDIO &lt;rel-path&gt;  - plays a .wav file
            ///   WAIT &lt;ms&gt;              - sleeps
            ///   RUN &lt;path&gt;            - runs an executable/bat
            ///   HIDE_ALL / SHOW N / HIDE N  - UI animation (logged, not rendered here)
            /// </summary>
            private static void RunAnimationScript(string scriptPath)
            {
                var lines = File.ReadAllLines(scriptPath);
                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    var spaceIdx = line.IndexOf(' ');
                    var directive = spaceIdx >= 0 ? line.Substring(0, spaceIdx).ToUpperInvariant() : line.ToUpperInvariant();
                    var arg       = spaceIdx >= 0 ? line.Substring(spaceIdx + 1).Trim() : "";

                    try
                    {
                        switch (directive)
                        {
                            case "OPEN_URL":
                                Log("[OPEN_URL] " + arg);
                                System.Diagnostics.Process.Start(arg);
                                break;

                            case "PLAY_AUDIO":
                                var audioPath = System.IO.Path.Combine(BaseDir, arg.Replace('/', System.IO.Path.DirectorySeparatorChar));
                                Log("[PLAY_AUDIO] " + audioPath);
                                if (File.Exists(audioPath))
                                {
                                    var player = new System.Media.SoundPlayer(audioPath);
                                    player.Play();
                                }
                                else
                                    Log("[WARN] Audio file not found: " + audioPath);
                                break;

                            case "WAIT":
                                int ms;
                                if (int.TryParse(arg, out ms))
                                    System.Threading.Thread.Sleep(ms);
                                break;

                            case "RUN":
                                var runPath = System.IO.Path.Combine(BaseDir, arg.Replace('/', System.IO.Path.DirectorySeparatorChar));
                                Log("[RUN] " + runPath);
                                System.Diagnostics.Process.Start(runPath);
                                break;

                            case "HIDE_ALL":
                            case "SHOW":
                            case "HIDE":
                                // UI animation directives – handled by the game's own renderer
                                break;

                            default:
                                Log("[SCRIPT] Unknown directive: " + directive);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("[ERROR] Script directive '" + directive + "' failed: " + ex.Message);
                    }
                }
            }

            // ── Helpers ───────────────────────────────────────────────────

            private static void EnsureInputFile()
            {
                try { System.IO.Directory.CreateDirectory(BaseDir); } catch { }

                if (!File.Exists(InputFile))
                    File.WriteAllText(InputFile, "");

                if (!File.Exists(CommandsFile))
                    Log("[WARN] custom-commands/commands.txt not found – no commands will be matched.");
            }

            private static void Log(string msg)
            {
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
                Console.WriteLine(line);
                try { File.AppendAllText(LogFile, line + "\n"); }
                catch { /* ignore log-write failures */ }
            }
        }
        """;
}
