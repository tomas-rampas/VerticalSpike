using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run -- <path-to-yaml>");
    return 1;
}

var yamlText = File.ReadAllText(args[0]);

// Spike-3 measurement flags. Without --measure the run is the unchanged verdict + smoke path.
var measure = args.Contains("--measure");
var cycles  = ArgInt(args, "--cycles", 50);
var batchN  = ArgInt(args, "--n",      200);
var warmup  = ArgInt(args, "--warmup", 5);
var csvPath = ArgStr(args, "--csv",    "spike3-memory.csv");

// Detect which YAML shape: multi.yaml uses "services:" (plural); healthcheck.yaml uses "service:" (singular).
var isMulti = yamlText.Contains("\nservices:") || yamlText.StartsWith("services:");

var deserializer = new DeserializerBuilder()
    .WithNamingConvention(CamelCaseNamingConvention.Instance)
    .IgnoreUnmatchedProperties()
    .Build();

TestPlan plan;
if (isMulti)
{
    plan = deserializer.Deserialize<TestPlan>(yamlText);
}
else
{
    // Spike-1 shape: singular service/step — adapt into the multi-provider path.
    var legacy = deserializer.Deserialize<LegacyPlan>(yamlText);
    plan = new TestPlan
    {
        Services =
        [
            new ServiceSpec { Name = legacy.Service.Name, Image = legacy.Service.Image, Kind = "container" }
        ],
        Steps =
        [
            new StepSpec
            {
                Name    = "healthcheck",
                Type    = "http.rest",
                Service = legacy.Service.Name,
                Method  = legacy.Step.Method,
                Path    = legacy.Step.Path,
                ExpectStatus = legacy.Step.ExpectStatus
            }
        ]
    };
}

// ── Aspire host ─────────────────────────────────────────────────────────────────────────

var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
{
    // Without this a headless dotnet run throws in BeforeStart because the dashboard
    // expects ASPNETCORE_URLS / ASPIRE_DASHBOARD_OTLP_* env-vars that only `aspire run` injects.
    DisableDashboard = true,
});

// Track builders by service name so we can resolve endpoints / connection-strings after start.
var containerBuilders = new Dictionary<string, IResourceBuilder<ContainerResource>>();
var pgDbBuilders = new Dictionary<string, IResourceBuilder<PostgresDatabaseResource>>();

foreach (var svc in plan.Services)
{
    if (svc.Kind == "container")
    {
        containerBuilders[svc.Name] = builder
            .AddContainer(svc.Name, svc.Image!)
            .WithHttpEndpoint(targetPort: 80);
    }
    else if (svc.Kind == "postgres")
    {
        pgDbBuilders[svc.Name] = builder
            .AddPostgres(svc.Name)
            .AddDatabase(svc.Database!);
    }
}

builder.Services.AddLogging(lb =>
{
    lb.AddFilter(null, LogLevel.Warning);
    lb.AddFilter("Microsoft.Extensions.Diagnostics.HealthChecks", LogLevel.None);
});

await using var app = builder.Build();
await app.StartAsync();

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

// Wait for every service to be healthy. Postgres has a real health check that actually gates;
// httpbin reaches Running (= healthy for a resource with no health check) almost immediately.
foreach (var svc in plan.Services)
{
    // For postgres: wait for the DATABASE resource (guarantees server + db both exist),
    // not the server resource — which is healthy before the db is created.
    var resourceName = svc.Kind == "postgres"
        ? pgDbBuilders[svc.Name].Resource.Name
        : svc.Name;
    await app.ResourceNotifications.WaitForResourceHealthyAsync(resourceName, cts.Token);
}

// Resolve runtime coordinates for each provider type.
var baseUrls        = new Dictionary<string, string>();
var connectionStrings = new Dictionary<string, string>();

foreach (var (name, cb) in containerBuilders)
    baseUrls[name] = cb.GetEndpoint("http").Url;

foreach (var (name, db) in pgDbBuilders)
    connectionStrings[name] = (await ((IResourceWithConnectionString)db.Resource).GetConnectionStringAsync(cts.Token))!;

// ── Provider routing ─────────────────────────────────────────────────────────────────────

var providers = new Dictionary<string, IStepProvider>
{
    ["http.rest"]          = new HttpRestStepProvider(),
    ["db-assert.postgres"] = new DbAssertPostgresStepProvider(),
};

// ── Fragment composition ──────────────────────────────────────────────────────────────────

var allImports    = new HashSet<string> { "System", "System.Linq", "System.Collections.Generic", "System.Threading.Tasks" };
var allRefs       = new HashSet<Assembly> { typeof(Console).Assembly, typeof(HttpClient).Assembly, typeof(Globals).Assembly };
var bodies        = new System.Text.StringBuilder();

bodies.AppendLine("var __results = new System.Collections.Generic.List<(string name, bool pass)>();");

foreach (var step in plan.Steps)
{
    if (!providers.TryGetValue(step.Type, out var provider))
        throw new InvalidOperationException($"No provider registered for step type '{step.Type}'.");

    var frag = provider.Emit(step);

    foreach (var imp in frag.Imports)    allImports.Add(imp);
    foreach (var asm in frag.References) allRefs.Add(asm);

    bodies.AppendLine("{");
    bodies.AppendLine(frag.Body);
    bodies.AppendLine("}");
}

bodies.AppendLine("return __results.All(r => r.pass);");

var composed = bodies.ToString();

// ── Compile the composed body into a collectible assembly (spike 3 execution path) ──────────
// The scripting run-path (CSharpScript.RunAsync) loads into the Default AssemblyLoadContext and
// cannot be made collectible, so the compile-once memory model is incompatible with it. Instead
// we wrap the composed body in a real class, build a CSharpCompilation, emit to bytes, and load
// those bytes into a collectible AssemblyLoadContext that can be unloaded. The provider Emit
// bodies are UNCHANGED: Http/BaseUrls/ConnectionStrings are hoisted into locals so the spliced
// body sees exactly the identifiers it saw as script globals.

var usings = string.Concat(allImports.Select(i => $"using {i};\n"));
var generatedSource =
    usings + "\n" +
    "public static class GeneratedTest\n" +
    "{\n" +
    "    public static async System.Threading.Tasks.Task<bool> RunAsync(Globals g)\n" +
    "    {\n" +
    "        var Http = g.Http;\n" +
    "        var BaseUrls = g.BaseUrls;\n" +
    "        var ConnectionStrings = g.ConnectionStrings;\n" +
    composed + "\n" +
    "    }\n" +
    "}\n";

var references = BuildReferences(allRefs);   // emit-once; reloaded into a fresh ALC per cycle
var realBytes  = Compile(generatedSource, references);

using var http = new HttpClient();
var globals = new Globals
{
    Http              = http,
    BaseUrls          = baseUrls,
    ConnectionStrings = connectionStrings,
};

if (!measure)
{
    // Default path (spike 1/2 regression): run once through the collectible context using the
    // exact NoInlining release boundary the measurement loop reuses, then prove the ALC unloads.
    var smokeWeak = RunBatchAndUnload(realBytes, globals, 1, out var pass);

    for (var i = 0; smokeWeak.IsAlive && i < 10; i++)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    Console.WriteLine(!smokeWeak.IsAlive
        ? "[smoke] collectible ALC unloaded after run (weak ref dead)"
        : "[smoke] WARNING: collectible ALC still alive after 10 forced GCs — did not unload");

    Console.WriteLine(pass ? "PASS" : "FAIL");
    return pass ? 0 : 1;
}

// ── Measurement harness (sitting two) ───────────────────────────────────────────────────────
// Control arm: same delegate / ALC / async-state-machine shape as the real assembly, but the body
// does no provider I/O. The real−control floor-slope difference therefore attributes any leak to
// the provider closure rather than to the ALC mechanics.
var controlSource =
    "public static class GeneratedTest\n" +
    "{\n" +
    "    public static async System.Threading.Tasks.Task<bool> RunAsync(Globals g)\n" +
    "    {\n" +
    "        await System.Threading.Tasks.Task.CompletedTask;\n" +
    "        return true;\n" +
    "    }\n" +
    "}\n";
var controlBytes = Compile(controlSource, references);

RunMeasurement(realBytes, controlBytes, globals, cycles, batchN, warmup, csvPath);
return 0;

// ── Local helpers ──────────────────────────────────────────────────────────────────────────

// Every dependency the generated assembly needs: the framework (trusted-platform assemblies),
// the fragment-declared references, the host assembly (for Globals), and Npgsql. The collectible
// context resolves all of these up to the Default context, so it holds only the generated glue.
static List<MetadataReference> BuildReferences(IEnumerable<Assembly> fragmentRefs)
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var refs = new List<MetadataReference>();

    void Add(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return;
        if (seen.Add(path) && File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
    }

    if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        foreach (var path in tpa.Split(Path.PathSeparator)) Add(path);

    foreach (var asm in fragmentRefs) Add(asm.Location);
    Add(typeof(Globals).Assembly.Location);
    Add(typeof(NpgsqlConnection).Assembly.Location);

    return refs;
}

// Compile a generated source string to a collectible-loadable assembly image (emit-once).
static byte[] Compile(string source, List<MetadataReference> references)
{
    var compilation = CSharpCompilation.Create(
        assemblyName: "VerticalSpike.Generated",
        syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
        references:  references,
        options:     new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

    using var peStream = new MemoryStream();
    var emitResult = compilation.Emit(peStream);
    if (!emitResult.Success)
    {
        var errors = emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
        throw new InvalidOperationException(
            "Generated assembly failed to compile:\n" + string.Join("\n", errors));
    }
    return peStream.ToArray();
}

// Create a collectible context, load the compiled assembly, invoke it n times, drop the delegate,
// initiate unload, and return only a weak reference. NoInlining guarantees the load context, the
// loaded assembly, and the delegate cannot remain rooted in this frame once it returns — the same
// boundary the smoke test (n = 1) and every measurement cycle (n = batch size) rely on.
[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference RunBatchAndUnload(byte[] asmBytes, Globals globals, int n, out bool allPass)
{
    var alc      = new CollectibleScriptContext();
    var assembly = alc.LoadFromStream(new MemoryStream(asmBytes));

    Func<Globals, Task<bool>>? run = assembly
        .GetType("GeneratedTest")!
        .GetMethod("RunAsync")!
        .CreateDelegate<Func<Globals, Task<bool>>>();

    allPass = true;
    for (var i = 0; i < n; i++)
        allPass &= run!(globals).GetAwaiter().GetResult();   // sequential — no connection-pool contention

    run = null;   // drop the only strong reference into the load context before unloading

    var weakRef = new WeakReference(alc, trackResurrection: true);
    alc.Unload();
    return weakRef;
}

// Run one measurement cycle: execute a batch in a fresh collectible context, force the unload,
// then capture the metric vector. preGc is read BEFORE the forced collection (a peak proxy);
// every floor reading is taken AFTER the bounded GC loop has confirmed (or given up on) unload.
static Row RunCycle(byte[] bytes, Globals globals, int n, string arm, int cycle, Process proc)
{
    var sw   = Stopwatch.StartNew();
    var weak = RunBatchAndUnload(bytes, globals, n, out var allPass);
    sw.Stop();

    var preGc = GC.GetTotalMemory(forceFullCollection: false);

    var gcIters = 0;
    for (; weak.IsAlive && gcIters < 10; gcIters++)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }
    var unloaded = !weak.IsAlive;

    var managedFloor = GC.GetTotalMemory(forceFullCollection: true);
    var info = GC.GetGCMemoryInfo();
    proc.Refresh();

    return new Row(
        Arm: arm, Cycle: cycle, N: n,
        PreGcManagedBytes: preGc, ManagedFloorBytes: managedFloor, GcHeapBytes: info.HeapSizeBytes,
        GcCommittedBytes: info.TotalCommittedBytes, GcFragmentedBytes: info.FragmentedBytes,
        PrivateBytes: proc.PrivateMemorySize64, WorkingSetBytes: proc.WorkingSet64,
        HandleCount: proc.HandleCount, LoadedAssemblies: AppDomain.CurrentDomain.GetAssemblies().Length,
        Gen0: GC.CollectionCount(0), Gen1: GC.CollectionCount(1), Gen2: GC.CollectionCount(2),
        Unloaded: unloaded, GcItersToUnload: gcIters, BatchMillis: sw.ElapsedMilliseconds, AllPass: allPass);
}

// Two-arm measurement, control first. The providers' per-step Console output is silenced for the
// duration so it neither floods the console nor pollutes batch_millis.
static void RunMeasurement(byte[] realBytes, byte[] controlBytes, Globals globals,
                           int cycles, int n, int warmup, string csvPath)
{
    var proc   = Process.GetCurrentProcess();
    var stdout = Console.Out;

    using var writer = new StreamWriter(csvPath, append: false);
    writer.WriteLine($"# spike3 | isServerGC={GCSettings.IsServerGC} latencyMode={GCSettings.LatencyMode} " +
                     $"runtime={Environment.Version} n={n} cycles={cycles} warmup={warmup}");
    writer.WriteLine("arm,cycle,n,pre_gc_managed_bytes,managed_floor_bytes,gc_heap_bytes," +
                     "gc_committed_bytes,gc_fragmented_bytes,private_bytes,working_set_bytes," +
                     "handle_count,loaded_assemblies,gen0,gen1,gen2,unloaded,gc_iters_to_unload," +
                     "batch_millis,all_pass");

    var control = new List<Row>(cycles);
    var real    = new List<Row>(cycles);

    Console.SetOut(TextWriter.Null);
    try
    {
        for (var c = 0; c < cycles; c++) { var r = RunCycle(controlBytes, globals, n, "control", c, proc); control.Add(r); WriteRow(writer, r); }
        for (var c = 0; c < cycles; c++) { var r = RunCycle(realBytes,    globals, n, "real",    c, proc); real.Add(r);    WriteRow(writer, r); }
    }
    finally
    {
        Console.SetOut(stdout);
    }

    writer.Flush();
    PrintHeadline(control, real, n, warmup, cycles, csvPath);
}

static void WriteRow(StreamWriter w, Row r) =>
    w.WriteLine($"{r.Arm},{r.Cycle},{r.N},{r.PreGcManagedBytes},{r.ManagedFloorBytes},{r.GcHeapBytes}," +
                $"{r.GcCommittedBytes},{r.GcFragmentedBytes},{r.PrivateBytes},{r.WorkingSetBytes}," +
                $"{r.HandleCount},{r.LoadedAssemblies},{r.Gen0},{r.Gen1},{r.Gen2},{r.Unloaded}," +
                $"{r.GcItersToUnload},{r.BatchMillis},{r.AllPass}");

// Ordinary-least-squares slope of y vs x, with R² and the standard error of the slope.
static (double slope, double r2, double stdErr) Ols(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
{
    var m = xs.Count;
    if (m < 3) return (double.NaN, double.NaN, double.NaN);

    double sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
    for (var i = 0; i < m; i++)
    {
        sx += xs[i]; sy += ys[i];
        sxx += xs[i] * xs[i]; sxy += xs[i] * ys[i]; syy += ys[i] * ys[i];
    }

    var sxxCentered = sxx - sx * sx / m;
    if (sxxCentered == 0) return (double.NaN, double.NaN, double.NaN);

    var slope     = (sxy - sx * sy / m) / sxxCentered;
    var intercept = (sy - slope * sx) / m;

    double ssRes = 0;
    for (var i = 0; i < m; i++)
    {
        var pred = intercept + slope * xs[i];
        ssRes += (ys[i] - pred) * (ys[i] - pred);
    }
    var ssTot  = syy - sy * sy / m;
    var r2     = ssTot == 0 ? double.NaN : 1.0 - ssRes / ssTot;
    var stdErr = Math.Sqrt(ssRes / (m - 2) / sxxCentered);
    return (slope, r2, stdErr);
}

static void PrintHeadline(List<Row> control, List<Row> real, int n, int warmup, int cycles, string csvPath)
{
    (double slope, double r2, double stdErr) Slope(List<Row> rows, Func<Row, double> sel) =>
        Ols(rows.Skip(warmup).Select(r => (double)r.Cycle).ToArray(),
            rows.Skip(warmup).Select(sel).ToArray());

    var cFloor = Slope(control, r => r.ManagedFloorBytes);
    var rFloor = Slope(real,    r => r.ManagedFloorBytes);
    var diff   = rFloor.slope - cFloor.slope;

    Console.WriteLine();
    Console.WriteLine($"── spike3 memory harness ── {cycles} cycles × {n} exec/cycle, first {warmup} dropped ──");
    Console.WriteLine($"CSV: {csvPath}");
    Console.WriteLine();
    Console.WriteLine("managed-floor slope (bytes/cycle, post-unload):");
    Console.WriteLine($"  control = {cFloor.slope,14:N1}  ± {cFloor.stdErr:N1}   (R²={cFloor.r2:F3})");
    Console.WriteLine($"  real    = {rFloor.slope,14:N1}  ± {rFloor.stdErr:N1}   (R²={rFloor.r2:F3})");
    Console.WriteLine($"  provider-attributed (real−control) = {diff:N1} bytes/cycle ≈ {diff / n:N2} bytes/execution");
    Console.WriteLine();
    Console.WriteLine($"private-bytes slope:  control={Slope(control, r => r.PrivateBytes).slope:N1}  real={Slope(real, r => r.PrivateBytes).slope:N1} bytes/cycle");
    Console.WriteLine($"handle-count slope:   control={Slope(control, r => r.HandleCount).slope:N3}  real={Slope(real, r => r.HandleCount).slope:N3} /cycle");
    Console.WriteLine($"loaded-asm slope:     control={Slope(control, r => r.LoadedAssemblies).slope:N3}  real={Slope(real, r => r.LoadedAssemblies).slope:N3} /cycle  (≈0 ⇒ assemblies unload)");
    Console.WriteLine($"unload success:       control={control.Count(r => r.Unloaded)}/{control.Count}  real={real.Count(r => r.Unloaded)}/{real.Count}");
}

static int ArgInt(string[] args, string name, int fallback)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var v) ? v : fallback;
}

static string ArgStr(string[] args, string name, string fallback)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : fallback;
}

// ── Model ──────────────────────────────────────────────────────────────────────────────────

public sealed class TestPlan
{
    public List<ServiceSpec> Services { get; set; } = [];
    public List<StepSpec>    Steps    { get; set; } = [];
}

public sealed class ServiceSpec
{
    public string  Name     { get; set; } = "";
    public string  Kind     { get; set; } = "container";
    public string? Image    { get; set; }
    public string? Database { get; set; }
}

// Loose superset node — every provider binds only the fields it needs.
public sealed class StepSpec
{
    public string Name         { get; set; } = "";
    public string Type         { get; set; } = "";
    public string Service      { get; set; } = "";
    public string? Method      { get; set; }
    public string? Path        { get; set; }
    public int    ExpectStatus { get; set; }
    public string? Query       { get; set; }
    public long   ExpectScalar { get; set; }
}

// Legacy spike-1 model (singular service/step keys).
public sealed class LegacyPlan
{
    public LegacyServiceSpec Service { get; set; } = new();
    public LegacyStepSpec    Step    { get; set; } = new();
}

public sealed class LegacyServiceSpec
{
    public string Name  { get; set; } = "";
    public string Image { get; set; } = "";
}

public sealed class LegacyStepSpec
{
    public string Method      { get; set; } = "";
    public string Path        { get; set; } = "";
    public int    ExpectStatus { get; set; }
}

// ── T1: the composition unit ───────────────────────────────────────────────────────────────

public record CsxFragment(
    IReadOnlyList<string>   Imports,
    IReadOnlyList<Assembly> References,
    string                  Body);

// ── Provider contract ──────────────────────────────────────────────────────────────────────

public interface IStepProvider
{
    string      Type { get; }
    CsxFragment Emit(StepSpec step);
}

public sealed class HttpRestStepProvider : IStepProvider
{
    public string Type => "http.rest";

    public CsxFragment Emit(StepSpec step)
    {
        // Step names like "orders-up" contain hyphens; sanitize to a valid C# identifier for variable suffixes.
        var id = step.Name.Replace("-", "_").Replace(".", "_");
        // $$"""...""" uses {{ }} as literal brace escapes; {id} is the only interpolation hole.
        // The CSX emitted uses $"..." interpolation, so {__verdict_id} etc. are CSX holes.
        var body = $$"""
            var __resp_{{id}}    = await Http.GetAsync(BaseUrls["{{step.Service}}"] + "{{step.Path}}");
            var __status_{{id}}  = (int)__resp_{{id}}.StatusCode;
            var __pass_{{id}}    = __status_{{id}} == {{step.ExpectStatus}};
            var __verdict_{{id}} = __pass_{{id}} ? "PASS" : "FAIL";
            Console.WriteLine($"[{{step.Name}}] {{step.Type}} -> {__verdict_{{id}}} (status {__status_{{id}}})");
            __results.Add(("{{step.Name}}", __pass_{{id}}));
            """;
        return new(
            Imports:    ["System.Net.Http"],
            References: [typeof(HttpClient).Assembly],
            Body:       body);
    }
}

public sealed class DbAssertPostgresStepProvider : IStepProvider
{
    public string Type => "db-assert.postgres";

    public CsxFragment Emit(StepSpec step)
    {
        var id = step.Name.Replace("-", "_").Replace(".", "_");
        var body = $$"""
            var __conn_{{id}}    = new NpgsqlConnection(ConnectionStrings["{{step.Service}}"]);
            await __conn_{{id}}.OpenAsync();
            var __cmd_{{id}}     = new NpgsqlCommand("{{step.Query}}", __conn_{{id}});
            var __scalar_{{id}}  = await __cmd_{{id}}.ExecuteScalarAsync();
            __conn_{{id}}.Dispose(); __cmd_{{id}}.Dispose();
            var __pass_{{id}}    = Convert.ToInt64(__scalar_{{id}}) == {{step.ExpectScalar}}L;
            var __verdict_{{id}} = __pass_{{id}} ? "PASS" : "FAIL";
            Console.WriteLine($"[{{step.Name}}] {{step.Type}} -> {__verdict_{{id}}} (scalar {__scalar_{{id}}})");
            __results.Add(("{{step.Name}}", __pass_{{id}}));
            """;
        return new(
            Imports:    ["Npgsql"],
            References: [typeof(NpgsqlConnection).Assembly],
            Body:       body);
    }
}

// ── Globals (the script host boundary — two fields stop here per the spike scope) ──────────

public sealed class Globals
{
    public HttpClient                         Http              = null!;
    public IReadOnlyDictionary<string,string> BaseUrls          = null!;
    public IReadOnlyDictionary<string,string> ConnectionStrings = null!;
}

// ── Per-cycle measurement row (spike 3, sitting two) ───────────────────────────────────────────
public readonly record struct Row(
    string Arm, int Cycle, int N,
    long PreGcManagedBytes, long ManagedFloorBytes, long GcHeapBytes,
    long GcCommittedBytes,  long GcFragmentedBytes,  long PrivateBytes, long WorkingSetBytes,
    int  HandleCount, int LoadedAssemblies, int Gen0, int Gen1, int Gen2,
    bool Unloaded, int GcItersToUnload, long BatchMillis, bool AllPass);

// ── Collectible load context for the compiled test assembly (spike 3) ──────────────────────────
// Holds only the generated glue assembly; returning null from Load makes every dependency
// (Globals, HttpClient, Npgsql, the BCL) resolve up to the Default context. Keeping the heavy,
// statically-stateful provider code out of this context is what should let it unload cleanly.
public sealed class CollectibleScriptContext : AssemblyLoadContext
{
    public CollectibleScriptContext() : base(isCollectible: true) { }
    protected override Assembly? Load(AssemblyName assemblyName) => null;
}
