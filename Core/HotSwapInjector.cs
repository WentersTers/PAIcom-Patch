using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;

namespace PAIcomPatcher.Core;

/// <summary>
/// Compiles the <see cref="HotSwapTemplate"/> C# source to IL via Roslyn,
/// then grafts the resulting type into the target module using dnlib.
///
/// Key public operations:
///   • <see cref="InjectHotSwapType"/>  – add the PAIcomPatcher.HotSwapRuntime type.
///   • <see cref="InjectWatcherStart"/> – prepend a call to StartWatcher() in an
///     existing init method.
///   • <see cref="PatchCommandHandler"/>– insert a routing call so that commands
///     written to command_input.txt are dispatched through our runtime.
/// </summary>
public class HotSwapInjector
{
    private readonly ModuleDef _targetModule;
    private readonly bool      _verbose;

    public HotSwapInjector(ModuleDef targetModule, bool verbose = false)
    {
        _targetModule = targetModule;
        _verbose      = verbose;
    }

    // ── 1. Inject the HotSwap type ────────────────────────────────────────

    /// <summary>
    /// Compiles <see cref="HotSwapTemplate.SourceCode"/> with Roslyn,
    /// loads the resulting in-memory assembly, then copies every type it
    /// declares into <see cref="_targetModule"/>.
    /// Returns the imported <see cref="TypeDef"/> for HotSwapRuntime.
    /// </summary>
    public TypeDef InjectHotSwapType()
    {
        Log("Compiling HotSwap template with Roslyn …");

        // ── Roslyn compile ────────────────────────────────────────────────
        // Parse as C# 7.3 – .NET Framework 4.8 does not support C# 8+ features
        // (nullable reference types, range/index operators, etc.)
        var parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.CSharp7_3);
        var syntaxTree = CSharpSyntaxTree.ParseText(
            HotSwapTemplate.SourceCode, parseOptions);
        var references = BuildReferences();
        var compilation = CSharpCompilation.Create(
            "HotSwapTemplate",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emitResult = compilation.Emit(ms);

        if (!emitResult.Success)
        {
            var errors = string.Join("\n",
                emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString()));
            throw new InvalidOperationException(
                $"HotSwap template failed to compile:\n{errors}");
        }

        ms.Seek(0, SeekOrigin.Begin);

        // ── Load compiled assembly with dnlib ─────────────────────────────
        Log("Loading compiled HotSwap assembly with dnlib …");
        var templateModule = ModuleDefMD.Load(ms, _targetModule.Context);

        // ── Copy every type declared in the template ───────────────────────
        TypeDef? hotSwapType = null;

        foreach (var typeDef in templateModule.Types.ToList())
        {
            if (typeDef.FullName == "<Module>") continue;

            // Duplicate the TypeDef into the target module's importer
            templateModule.Types.Remove(typeDef);
            _targetModule.Types.Add(typeDef);

            Log($"  Injected type: {typeDef.FullName}");

            if (typeDef.Name == "HotSwapRuntime")
                hotSwapType = typeDef;
        }

        if (hotSwapType is null)
            throw new InvalidOperationException(
                "Template compiled successfully but HotSwapRuntime type was not found.");

        return hotSwapType;
    }

    // ── 2. Prepend StartWatcher() call to an init method ──────────────────

    /// <summary>
    /// Inserts   call HotSwapRuntime::StartWatcher()
    /// into <paramref name="initMethod"/> AFTER the base constructor call.
    ///
    /// Injecting before base..ctor() is called causes a VerificationException.
    /// We find the first call/callvirt in the method body (which is base..ctor
    /// in a constructor) and insert immediately after it.  For non-constructors
    /// we insert before the first non-nop instruction instead.
    /// </summary>
    public void InjectWatcherStart(MethodDef initMethod, TypeDef hotSwapType)
    {
        var startWatcher = hotSwapType.Methods
            .FirstOrDefault(m => m.Name == "StartWatcher" && m.IsStatic)
            ?? throw new InvalidOperationException("HotSwapRuntime.StartWatcher not found.");

        var body   = initMethod.Body;
        var instrs = body.Instructions;

        // Import the method reference into the target module
        var methodRef = _targetModule.Import(startWatcher) as IMethod
            ?? throw new InvalidOperationException("Could not import StartWatcher reference.");

        // Find insertion point:
        //   • For .ctor: right after the first call/callvirt (= base..ctor()).
        //   • For other methods: right after the first non-nop instruction.
        int insertAt = 0;
        if (initMethod.IsConstructor)
        {
            for (int i = 0; i < instrs.Count; i++)
            {
                var op = instrs[i].OpCode.Code;
                if (op is Code.Call or Code.Callvirt)
                {
                    insertAt = i + 1; // after base..ctor()
                    break;
                }
            }
        }
        else
        {
            for (int i = 0; i < instrs.Count; i++)
            {
                if (instrs[i].OpCode != OpCodes.Nop)
                {
                    insertAt = i;
                    break;
                }
            }
        }

        instrs.Insert(insertAt, OpCodes.Call.ToInstruction(methodRef));

        // Re-compute offsets and exception handler ranges
        body.UpdateInstructionOffsets();

        Log($"  Injected StartWatcher() at position {insertAt} in {initMethod.FullName}");
    }

    // ── 3. Patch the command handler ──────────────────────────────────────

    /// <summary>
    /// Modifies the command handler so that after its own dispatch logic, it
    /// also notifies HotSwapRuntime that a command was invoked.
    ///
    /// The injected IL pattern (appended before every ret):
    ///   ldarg.0 (or ldloc that holds the command string – auto-detected)
    ///   call HotSwapRuntime::OnCommandDispatched(string)
    /// </summary>
    public void PatchCommandHandler(MethodDef handler)
    {
        // Find HotSwapRuntime.OnCommandDispatched in the module
        var runtimeType = _targetModule.Types
            .FirstOrDefault(t => t.Name == "HotSwapRuntime")
            ?? throw new InvalidOperationException(
                "HotSwapRuntime not found – inject it before calling PatchCommandHandler.");

        var onCmd = runtimeType.Methods
            .FirstOrDefault(m => m.Name == "OnCommandDispatched")
            ?? throw new InvalidOperationException("HotSwapRuntime.OnCommandDispatched not found.");

        var cmdRef = _targetModule.Import(onCmd) as IMethod
            ?? throw new InvalidOperationException("Could not import OnCommandDispatched.");

        var body   = handler.Body;
        var instrs = body.Instructions;

        // Detect which local/arg holds the current command string.
        // Heuristic: the first ldarg or ldloc right after a string comparison.
        int? cmdArgIndex = DetectCommandStringArg(handler);

        // Insert notification before every 'ret'
        int insertCount = 0;
        for (int i = instrs.Count - 1; i >= 0; i--)
        {
            if (instrs[i].OpCode != OpCodes.Ret) continue;

            // Load command string
            if (cmdArgIndex.HasValue)
                instrs.Insert(i, OpCodes.Ldarg.ToInstruction(
                    handler.Parameters[cmdArgIndex.Value]));
            else
                instrs.Insert(i, OpCodes.Ldnull.ToInstruction());

            // Call notification
            instrs.Insert(i + 1, OpCodes.Call.ToInstruction(cmdRef));

            insertCount++;
        }

        body.UpdateInstructionOffsets();
        Log($"  Patched {insertCount} ret(s) in command handler {handler.FullName}");
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private static int? DetectCommandStringArg(MethodDef method)
    {
        // Walk parameters: find the first string-typed one
        for (int i = 0; i < method.Parameters.Count; i++)
        {
            var p = method.Parameters[i];
            if (p.Type.FullName == "System.String")
                return i;
        }
        return null;
    }

    private static MetadataReference[] BuildReferences()
    {
        // PAIcom.exe targets .NET Framework 4.x (mscorlib 4.0).
        // We must compile HotSwapTemplate against the same Framework assemblies,
        // NOT against the .NET 8 runtime that powers this patcher tool.
        // The canonical location for .NET Framework 4.x reference assemblies is:
        //   C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\

        var refAsmRoots = new[]
        {
            @"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8",
            @"C:\Program Files\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8",
            @"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2",
            // Fallback: live GAC (always present if .NET Framework 4.x is installed)
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
                .Replace(@"\Microsoft.NET\Framework64\", @"\Microsoft.NET\Framework\")
                .TrimEnd('\\'),
        };

        // Assemblies needed by HotSwapTemplate on .NET Framework 4.x
        string[] needed =
        [
            "mscorlib.dll",             // object, string, Thread, etc.
            "System.dll",               // FileSystemWatcher, FileInfo, Process, etc.
            "System.Core.dll",          // LINQ, Func<>, etc.
            "System.Windows.Forms.dll", // Application.OpenForms
        ];

        var refs = new List<MetadataReference>();

        foreach (var root in refAsmRoots)
        {
            if (!Directory.Exists(root)) continue;

            bool allFound = true;
            var batch = new List<MetadataReference>();

            foreach (var dll in needed)
            {
                var path = Path.Combine(root, dll);
                if (!File.Exists(path)) { allFound = false; break; }
                batch.Add(MetadataReference.CreateFromFile(path));
            }

            if (allFound)
            {
                refs.AddRange(batch);
                Console.WriteLine($"    [inject] Framework refs from: {root}");
                return [.. refs];
            }
        }

        // Last-ditch fallback: grab mscorlib from wherever .NET Framework loaded it.
        // Assembly.Location returns "" in single-file publishes — this path is only
        // reached if no .NETFramework reference-assembly directory was found, which
        // would mean the user can't run PAIcom anyway.
#pragma warning disable IL3000
        var mscorlibPath = typeof(object).Assembly.Location;
#pragma warning restore IL3000
        if (File.Exists(mscorlibPath))
            refs.Add(MetadataReference.CreateFromFile(mscorlibPath));

        // And System.dll sibling
        var systemPath = Path.Combine(
            Path.GetDirectoryName(mscorlibPath)!, "System.dll");
        if (File.Exists(systemPath))
            refs.Add(MetadataReference.CreateFromFile(systemPath));

        return [.. refs];
    }

    private void Log(string msg)
    {
        if (_verbose) Console.WriteLine($"    [inject] {msg}");
    }
}
