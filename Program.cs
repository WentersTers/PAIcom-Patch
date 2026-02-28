using PAIcomPatcher.Core;
using System.Security.Cryptography;

namespace PAIcomPatcher;

/// <summary>
/// Entry point.  Usage:
///   PAIcomPatcher.exe &lt;path-to-PAIcom.exe&gt; [options]
///
///   --out &lt;file&gt;    Output path (default: &lt;input&gt;.patched.exe)
///   --dry-run       Analyse and report patch points without writing output
///   --backup        Copy original to &lt;input&gt;.bak before patching
///   --verbose       Print detailed IL scan progress
/// </summary>
class Program
{
    static int Main(string[] args)
    {
        Console.WriteLine("PAIcom Binary-Patch Injector v1.0");
        Console.WriteLine("==================================");

        // ── Parse arguments ──────────────────────────────────────────────
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        var inputPath  = args[0];
        string? outPath = null;
        bool dryRun    = false;
        bool backup    = false;
        bool verbose   = false;
        bool analyze   = false;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":      outPath = args[++i]; break;
                case "--dry-run":  dryRun  = true;      break;
                case "--backup":   backup  = true;      break;
                case "--verbose":  verbose = true;      break;
                case "--analyze":  analyze = true;      break;
                default:
                    Console.Error.WriteLine($"[WARN] Unknown argument: {args[i]}");
                    break;
            }
        }

        outPath ??= inputPath + ".patched.exe";

        // ── Validate input ───────────────────────────────────────────────
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"[ERROR] File not found: {inputPath}");
            return 1;
        }

        // Print SHA-256 so the user can confirm which build they're patching
        byte[] rawBytes;
        try
        {
            using var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            rawBytes = new byte[fs.Length];
            fs.ReadExactly(rawBytes);
        }
        catch (IOException ioEx)
        {
            Console.Error.WriteLine($"[ERROR] Cannot read {inputPath}: {ioEx.Message}");
            Console.Error.WriteLine("       Make sure the application is NOT running before patching.");
            return 1;
        }
        var sha256 = Convert.ToHexString(SHA256.HashData(rawBytes));
        Console.WriteLine($"Input   : {inputPath}");
        Console.WriteLine($"SHA-256 : {sha256}");
        Console.WriteLine($"Output  : {(dryRun ? "(dry-run – no write)" : outPath)}");
        Console.WriteLine();

        // ── Analyze mode (early exit) ────────────────────────────────────
        if (analyze)
        {
            AssemblyAnalyzer.Analyze(inputPath);
            return 0;
        }

        // ── Optional backup ──────────────────────────────────────────────
        if (backup && !dryRun)
        {
            var bakPath = inputPath + ".bak";
            File.Copy(inputPath, bakPath, overwrite: true);
            Console.WriteLine($"[INFO] Backup written to {bakPath}");
        }

        // ── Run patcher ──────────────────────────────────────────────────
        try
        {
            var patcher = new AssemblyPatcher(verbose);
            var result  = patcher.Patch(inputPath, outPath, dryRun);

            Console.WriteLine();
            Console.WriteLine("── Patch Results ───────────────────────────────────────────────");
            Console.WriteLine($"  Methods scanned   : {result.MethodsScanned}");
            Console.WriteLine($"  Patch points found: {result.PatchPointsFound}");
            Console.WriteLine($"  Patch points hit  : {result.PatchPointsApplied}");

            if (result.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("[ERRORS]");
                foreach (var e in result.Errors)
                    Console.Error.WriteLine($"  {e}");
                return 2;
            }

            if (dryRun)
            {
                Console.WriteLine();
                Console.WriteLine("[DRY-RUN] No file written.  All patch points located successfully.");
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine($"[OK] Patched assembly written to: {outPath}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\n[FATAL] {ex.GetType().Name}: {ex.Message}");
            if (verbose)
                Console.Error.WriteLine(ex.StackTrace);
            return 3;
        }
    }

    static void PrintHelp()
    {
        Console.WriteLine("""
        Usage:
          PAIcomPatcher.exe <path-to-PAIcom.exe> [options]

        Options:
          --out <file>    Output path  (default: <input>.patched.exe)
          --dry-run       Find patch points without writing output
          --backup        Write <input>.bak before patching
          --verbose       Detailed IL scan output

        Description:
          Injects a FileSystemWatcher-based hot-swap module into the target
          assembly.  The watcher monitors command_input.txt, looks up the
          command in commands.txt, and invokes the corresponding in-game
          method at runtime – without restarting the process.
        """);
    }
}
