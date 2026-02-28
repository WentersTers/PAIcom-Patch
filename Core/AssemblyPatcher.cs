using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace PAIcomPatcher.Core;

/// <summary>
/// Top-level orchestrator.  Loads the target module, runs each patch step,
/// and (optionally) writes the modified PE to disk.
/// </summary>
public class AssemblyPatcher
{
    private readonly bool _verbose;

    public AssemblyPatcher(bool verbose = false) => _verbose = verbose;

    /// <summary>Patch the assembly at <paramref name="inputPath"/>.</summary>
    public PatchResult Patch(string inputPath, string outputPath, bool dryRun = false)
    {
        var result = new PatchResult();

        // ── 1. Load module ────────────────────────────────────────────────
        Log("Loading module …");
        var ctx  = ModuleDef.CreateModuleContext();
        // Read into memory first so we don't hold an exclusive OS file lock
        byte[] peBytes;
        using (var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            peBytes = new byte[fs.Length];
            fs.ReadExactly(peBytes);
        }
        var module = ModuleDefMD.Load(peBytes, ctx);
        module.Context = ctx;

        // ── 2. Scan all methods BEFORE injecting new types ────────────────
        //   (prevents our own injected code from polluting the search pool)
        var allMethods = module.GetTypes()
                               .SelectMany(t => t.Methods)
                               .Where(m => m.HasBody)
                               .ToList();

        result.MethodsScanned = allMethods.Count;
        Log($"Scanned {allMethods.Count} methods.");

        // ── 3. Find command handler and init routine ───────────────────────
        Log("Searching for command handler by IL pattern …");
        var finder = new CommandHandlerFinder(module, _verbose);

        var cmdHandler = finder.FindCommandHandler();
        var initMethod = finder.FindInitializationMethod();

        if (cmdHandler is not null)
        {
            Log($"  Command handler : {cmdHandler.FullName}");
            result.PatchPointsFound++;
        }
        else
        {
            result.Errors.Add("Could not locate command handler (IL pattern not matched).");
        }

        if (initMethod is not null)
        {
            Log($"  Init method     : {initMethod.FullName}");
            result.PatchPointsFound++;
        }
        else
        {
            result.Errors.Add("Could not locate initialization method.");
        }

        // ── 5. Apply patches ───────────────────────────────────────────────
        if (!dryRun && result.Errors.Count == 0)
        {
            // Inject the HotSwap type now, AFTER scanning is complete
            Log("Injecting HotSwap type …");
            var injector    = new HotSwapInjector(module, _verbose);
            var hotSwapType = injector.InjectHotSwapType();

            if (cmdHandler is not null)
            {
                Log("Patching command handler …");
                injector.PatchCommandHandler(cmdHandler);
                result.PatchPointsApplied++;
            }

            if (initMethod is not null)
            {
                Log("Injecting StartWatcher call into init method …");
                injector.InjectWatcherStart(initMethod, hotSwapType);
                result.PatchPointsApplied++;
            }
        }
        else if (dryRun)
        {
            Log("(dry-run) Skipping write step.");
        }

        // ── 6. Write output ────────────────────────────────────────────────
        if (!dryRun && result.Errors.Count == 0)
        {
            Log($"Writing patched assembly to {outputPath} …");

            // Use the managed writer - NativeModuleWriter requires all existing
            // RIDs to be preserved which conflicts with newly added types.
            var writerOptions = new ModuleWriterOptions(module)
            {
                WritePdb = false,
            };

            module.Write(outputPath, writerOptions);
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void Log(string msg)
    {
        if (_verbose)
            Console.WriteLine($"  [V] {msg}");
        else
            Console.WriteLine($"  {msg}");
    }
}

/// <summary>Results returned after a patch run.</summary>
public class PatchResult
{
    public int MethodsScanned      { get; set; }
    public int PatchPointsFound    { get; set; }
    public int PatchPointsApplied  { get; set; }
    public List<string> Errors     { get; } = [];
}
