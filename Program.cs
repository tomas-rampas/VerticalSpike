using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run -- <path-to-yaml>");
    return 1;
}

var yamlText = File.ReadAllText(args[0]);

var deserializer = new DeserializerBuilder()
    .WithNamingConvention(CamelCaseNamingConvention.Instance)
    .Build();

var model = deserializer.Deserialize<TestPlan>(yamlText);

// A headless `dotnet run` AppHost throws in BeforeStart without this: the Aspire dashboard
// requires ASPNETCORE_URLS / ASPIRE_DASHBOARD_OTLP_* env vars that only `aspire run` injects.
var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
{
    DisableDashboard = true,
});

var service = builder
    .AddContainer(model.Service.Name, model.Service.Image)
    .WithHttpEndpoint(targetPort: 80);

await using var app = builder.Build();
await app.StartAsync();

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
await app.ResourceNotifications.WaitForResourceHealthyAsync(model.Service.Name, cts.Token);

var baseUrl = service.GetEndpoint("http").Url;

using var http = new HttpClient();
var globals = new Globals { BaseUrl = baseUrl, Http = http };

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

public sealed class Globals
{
    public string BaseUrl = "";
    public HttpClient Http = null!;
}
