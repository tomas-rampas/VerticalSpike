using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
// note: `Aspire.Hosting` is supplied as a global using by the Aspire.AppHost.Sdk.

// VerticalSpike — YAML-driven integration-test runner (two-evening spike).
// Reads a YAML test plan, provisions one container via Aspire, waits for it to report
// healthy, then compiles + runs a generated CSX step via Roslyn and prints PASS/FAIL.
// Errors deliberately propagate — the exception is the diagnostic.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run -- <path-to-yaml>");
    return 1;
}

var yamlText = File.ReadAllText(args[0]);

var deserializer = new DeserializerBuilder()
    .WithNamingConvention(CamelCaseNamingConvention.Instance) // maps expectStatus -> ExpectStatus
    .Build();

var model = deserializer.Deserialize<TestPlan>(yamlText);

// --- Provision the container via Aspire, start, wait healthy, resolve endpoint (Tasks 6-9) ---
// SPIKE FINDING: a bare CreateBuilder() + StartAsync() throws in BeforeStart because the
// Aspire dashboard demands ASPNETCORE_URLS / ASPIRE_DASHBOARD_OTLP_* env vars that the
// `aspire run` tooling normally injects. A headless `dotnet run` AppHost must opt out.
var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
{
    DisableDashboard = true,
});

var service = builder
    .AddContainer(model.Service.Name, model.Service.Image)
    .WithHttpEndpoint(targetPort: 80); // httpbin listens on 80 inside the container

await using var app = builder.Build();
await app.StartAsync();

// httpbin defines no health check, so Aspire treats "Running" as healthy
// (verified against the Aspire 13.x health-checks docs).
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
await app.ResourceNotifications.WaitForResourceHealthyAsync(model.Service.Name, cts.Token);

var baseUrl = service.GetEndpoint("http").Url;

// --- Generate the CSX step, compile + run via Roslyn, print verdict (Tasks 10-14) ---
using var http = new HttpClient();
var globals = new Globals { BaseUrl = baseUrl, Http = http };

// $$"""...""" uses {{ }} for host interpolation, so single-brace {observed} stays literal
// in the generated script. The script prints the observed code and returns the verdict.
var csx = $$"""
    var resp = await Http.GetAsync(BaseUrl + "{{model.Step.Path}}");
    var observed = (int)resp.StatusCode;
    Console.WriteLine($"observed status = {observed}");
    return observed == {{model.Step.ExpectStatus}};
    """;

var options = ScriptOptions.Default
    .WithReferences(typeof(Console).Assembly, typeof(HttpClient).Assembly, typeof(Globals).Assembly)
    .WithImports("System", "System.Net.Http");

var script = CSharpScript.Create<bool>(csx, options, typeof(Globals));
var state = await script.RunAsync(globals);
var pass = state.ReturnValue;

Console.WriteLine(pass ? "PASS" : "FAIL");
return pass ? 0 : 1;

// --- in-memory model (Task 4) ---
public sealed class TestPlan
{
    public ServiceSpec Service { get; set; } = new();
    public StepSpec Step { get; set; } = new();
}

public sealed class ServiceSpec
{
    public string Name { get; set; } = "";
    public string Image { get; set; } = "";
}

public sealed class StepSpec
{
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public int ExpectStatus { get; set; }
}

// --- the architectural seam: the future ScriptGlobalVariables. Keep it to two members (Task 10). ---
public sealed class Globals
{
    public string BaseUrl = "";
    public HttpClient Http = null!;
}
