using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PAIcomPatcher.Core;

/// <summary>
/// Locates the two methods we need to patch using STRUCTURAL IL metrics
/// only – no string literals are assumed because ConfuserEx encrypts them.
///
///   1. COMMAND HANDLER  – the method with the richest mix of branch
///      instructions and field accesses (= a large dispatcher / update loop).
///
///   2. INITIALIZATION METHOD – the constructor with the highest number of
///      outbound calls (= the one that wires everything up at startup).
/// </summary>
public class CommandHandlerFinder
{
    private readonly ModuleDef _module;
    private readonly bool      _verbose;
    private readonly List<MethodDef> _allMethods;

    // Structural scoring weights
    private const double WeightBranch = 2.0;
    private const double WeightField  = 1.5;
    private const double WeightCall   = 1.0;
    private const double WeightTotal  = 0.1;   // tie-breaker: sheer size

    public CommandHandlerFinder(ModuleDef module, bool verbose = false)
    {
        _module     = module;
        _verbose    = verbose;
        _allMethods = module.GetTypes()
                            .SelectMany(t => t.Methods)
                            .Where(m => m.HasBody)
                            .ToList();
    }

    // ── 1. Command handler ────────────────────────────────────────────────

    /// <summary>
    /// Find the method responsible for dispatching commands/animations.
    ///
    /// Strategy A – structural score (branch × weight + field × weight + …).
    ///   The game's dispatcher is the method with the most conditional logic
    ///   touching the most fields, regardless of string content.
    ///
    /// Strategy B – raw instruction count (monolithic update loop fallback).
    ///
    /// Strategy C – ldstr density (only useful if strings are NOT encrypted).
    /// </summary>
    public MethodDef? FindCommandHandler()
    {
        // Strategy A – weighted structural score (ignores string content)
        var byScore = _allMethods
            .Where(m => m.Body.Instructions.Count > 50) // skip trivial methods
            .Select(m =>
            {
                var instrs  = m.Body.Instructions;
                int total   = instrs.Count;
                int branches = instrs.Count(i =>
                    i.OpCode.FlowControl is FlowControl.Cond_Branch or FlowControl.Branch);
                int fields  = instrs.Count(i =>
                    i.OpCode == OpCodes.Stfld || i.OpCode == OpCodes.Ldfld);
                int calls   = instrs.Count(i =>
                    i.OpCode is { Code: Code.Call or Code.Callvirt });
                double score = branches * WeightBranch
                             + fields   * WeightField
                             + calls    * WeightCall
                             + total    * WeightTotal;
                return (method: m, score, total, branches, fields);
            })
            .OrderByDescending(x => x.score)
            .FirstOrDefault();

        if (byScore != default)
        {
            Log($"FindCommandHandler: strategy (A) structural score" +
                $" instrs={byScore.total} branches={byScore.branches}" +
                $" fields={byScore.fields} score={byScore.score:F0}" +
                $" → {byScore.method.FullName}");
            return byScore.method;
        }

        // Strategy B – largest method by instruction count
        var bySize = _allMethods
            .OrderByDescending(m => m.Body.Instructions.Count)
            .FirstOrDefault();

        if (bySize is not null)
        {
            Log($"FindCommandHandler: strategy (B) largest method → {bySize.FullName}");
            return bySize;
        }

        // Strategy C – ldstr density (unencrypted fallback)
        var byLdstr = _allMethods
            .Select(m => (method: m,
                          count: m.Body.Instructions.Count(i => i.OpCode == OpCodes.Ldstr)))
            .Where(x => x.count >= 3)
            .OrderByDescending(x => x.count)
            .FirstOrDefault();

        if (byLdstr != default)
        {
            Log($"FindCommandHandler: strategy (C) ldstr fallback → {byLdstr.method.FullName}");
            return byLdstr.method;
        }

        Log("FindCommandHandler: no candidate found.");
        return null;
    }

    // ── 2. Initialization method ──────────────────────────────────────────

    /// <summary>
    /// Find a safe one-time initialization point.
    ///
    /// Strategy A – constructor with the highest call count.
    ///   The .ctor that makes the most outbound calls is the one that
    ///   wires up all subsystems – ideal for inserting StartWatcher().
    ///
    /// Strategy B – constructor on the same type as the command handler.
    ///
    /// Strategy C – any static constructor (.cctor) on the main type.
    ///
    /// Strategy D – module entrypoint (last resort).
    /// </summary>
    public MethodDef? FindInitializationMethod()
    {
        // Strategy A – most-calls constructor (across all types)
        var bestCtor = _allMethods
            .Where(m => m.IsConstructor && m.HasBody)
            .Select(m => (method: m,
                          calls: m.Body.Instructions.Count(i =>
                              i.OpCode is { Code: Code.Call or Code.Callvirt })))
            .OrderByDescending(x => x.calls)
            .FirstOrDefault();

        if (bestCtor != default)
        {
            Log($"FindInitializationMethod: strategy (A) most-calls ctor" +
                $" (calls={bestCtor.calls}) → {bestCtor.method.FullName}");
            return bestCtor.method;
        }

        // Strategy B – ctor on same type as command handler
        var cmdHandler = FindCommandHandler();
        if (cmdHandler?.DeclaringType is { } cmdType)
        {
            var ctor = cmdType.Methods.FirstOrDefault(m => m.IsConstructor && m.HasBody);
            if (ctor is not null)
            {
                Log($"FindInitializationMethod: strategy (B) same-type ctor → {ctor.FullName}");
                return ctor;
            }
        }

        // Strategy C – static constructor on the main class (highest score type)
        var cctor = _allMethods
            .Where(m => m.IsStaticConstructor && m.HasBody)
            .OrderByDescending(m => m.Body.Instructions.Count)
            .FirstOrDefault();

        if (cctor is not null)
        {
            Log($"FindInitializationMethod: strategy (C) static ctor → {cctor.FullName}");
            return cctor;
        }

        // Strategy D – module entrypoint
        if (_module.EntryPoint is { HasBody: true } ep)
        {
            Log($"FindInitializationMethod: strategy (D) entrypoint → {ep.FullName}");
            return ep;
        }

        Log("FindInitializationMethod: no candidate found.");
        return null;
    }

    // ── Helper ────────────────────────────────────────────────────────────

    private void Log(string msg)
    {
        if (_verbose)
            Console.WriteLine($"    [scan] {msg}");
    }
}
