# CLAUDE.md

## What this project is

This is a **two-evening vertical-slice spike** for a YAML-driven integration-testing
platform for .NET microservices. The full vision is much larger (see the three
architecture documents in `/docs` if present), but this repository deliberately
contains only the irreducible spine of the architecture, end to end, so that
the founder can find out three things that documents cannot tell them:

1. Whether .NET Aspire's programmatic builder API behaves the way the
   architecture assumes.
2. Whether the YAML → CSX → execution loop produces something that feels like
   a usable test platform.
3. Whether building this thing solo is something the founder wants to spend
   evenings doing.

**It is not the start of the real project.** It is the cheapest possible
experiment that produces real evidence before any larger commitment is made.

## What "done" looks like for this spike

A single command — `dotnet run -- examples/healthcheck.yaml` — that:

1. Reads a hardcoded YAML file from disk describing one service and one HTTP
   step.
2. Provisions one container via .NET Aspire and waits for it to report healthy.
3. Generates a small CSX string from the YAML.
4. Compiles and runs the CSX once via the Roslyn scripting API.
5. Prints `PASS` or `FAIL` to the console with the verdict and the actual
   status code observed.
6. Exits with code 0 on PASS, 1 on FAIL.

That's it. No tests of its own, no README, no CI, no installer, no second
example file, no second step type, no captured variables, no providers, no
schema validation, no reporting, no parallelism, no runner. Resist the urge.

## Stack

- **.NET 10.** The project targets .NET 10 because that is the version the
  full platform will ship on, and the spike is the cheapest moment to find out
  whether anything in the toolchain behaves unexpectedly on .NET 10. Use the
  current GA build; do not run on a daily preview.
- **Aspire.Hosting** (the latest version compatible with .NET 10). Used for
  the programmatic builder (`DistributedApplication.CreateBuilder()`),
  `AddContainer`, `WaitFor`, and endpoint resolution via
  `GetEndpoint(name, scheme)`. Pin to a specific version in the `.csproj`; do
  not float.
- **Microsoft.CodeAnalysis.CSharp.Scripting** (Roslyn). Used for
  `CSharpScript.Create<bool>()` and `RunAsync`. The collectible-load-context
  pattern is **out of scope for the spike** — it matters for the full project
  but adds complexity that obscures the experiment's actual purpose. The spike
  uses straight `RunAsync` and accepts the leaked-assembly cost.
- **YamlDotNet** for parsing.
- **Docker Desktop** or Podman as the container runtime. Anything OCI-compatible
  is fine; the spike does not test the registry-resolution machinery.

Pin every package to a specific minor version in the `.csproj`. The point of
this spike is reproducibility of the experiment, not currency. A `latest`-floating
dependency turning over during the two evenings is the kind of incidental
problem the spike is specifically designed not to chase.

### .NET 10 specifics worth knowing before you start

- **Aspire on .NET 10.** Aspire's tooling and templates moved forward with the
  .NET 10 wave; the package versions and target framework moniker (`net10.0`)
  the spike uses should align. If `dotnet new aspire-apphost` is convenient,
  use it as a starting point and then strip the template down — but the spike's
  AppHost is constructed programmatically in code, not by adding `Projects.*`
  references. The architecture documents (Doc 1, §4.1) describe why.
- **Roslyn scripting on .NET 10.** `Microsoft.CodeAnalysis.CSharp.Scripting`
  has historically lagged the language version of the host runtime. If you hit
  a `LanguageVersion` mismatch — typical symptom: "feature 'X' is not available
  in C# N" when X is a new-ish language feature — set
  `scriptOptions = scriptOptions.WithLanguageVersion(LanguageVersion.Latest)`
  explicitly. This is exactly the kind of thing the spike exists to discover,
  so note it in `LEARNED.md` if it bites.
- **The architecture's runtime baseline is .NET 8 LTS minimum.** The spike
  targets .NET 10 as a deliberate forward bet, but the real platform will
  build against .NET 8 / 9 / 10 (architecture document, §5.7). The spike is
  not the place to enforce that breadth; it tests the top of the supported
  range.

## What the example YAML looks like

```yaml
# examples/healthcheck.yaml — the only example file the spike supports
service:
  name: orders-api
  image: kennethreitz/httpbin:latest

step:
  method: GET
  path: /status/200
  expectStatus: 200
```

`kennethreitz/httpbin` is the right image for this spike because its
`/status/{code}` endpoint always returns the requested code, so the experiment
isolates "does the platform work?" from "does the system under test work?"

## The shape of the code

A single executable with roughly this skeleton. Keep it in one file if you can;
add structure only when an editor warning forces you to.

```
Program.cs
├── Main(args)
│   ├── Read YAML from args[0]
│   ├── Deserialize into a small in-memory model
│   ├── Build the Aspire host:
│   │     var b = DistributedApplication.CreateBuilder();
│   │     b.AddContainer(model.Service.Name, model.Service.Image)
│   │      .WithHttpEndpoint(targetPort: 80);    // httpbin listens on 80
│   ├── await using var app = b.Build();
│   │   await app.StartAsync();
│   ├── Wait for healthy:
│   │     await app.ResourceNotifications
│   │              .WaitForResourceHealthyAsync(model.Service.Name);
│   ├── Resolve endpoint:
│   │     var url = app.GetEndpoint(model.Service.Name, "http");
│   ├── Build CSX string from the model (string interpolation is fine)
│   ├── Compile and run:
│   │     var script = CSharpScript.Create<bool>(csx, options, typeof(Globals));
│   │     var result = await script.RunAsync(new Globals { BaseUrl = url, ... });
│   ├── Print PASS / FAIL with the observed status code
│   └── Return verdict-appropriate exit code
└── public sealed class Globals { public string BaseUrl; public HttpClient Http; }
```

The `Globals` class is the architectural seam — it is the future
`ScriptGlobalVariables`. Keep it small (a `HttpClient` and a `BaseUrl` are
enough for this spike). Do not generalise it; the moment it has more than two
properties, the spike has scope-crept.

## Things to deliberately not do

Listed because they are the temptations that will appear, and naming them is
how you avoid them.

- **A second step type.** The spike is one HTTP step. Adding a Kafka step or
  a database assertion is the start of the provider model, which is a multi-week
  design effort, not a Friday-evening addition.
- **The collectible AssemblyLoadContext.** It belongs in the real project. The
  spike runs the script once and exits; the assembly leak is acceptable.
- **A JSON Schema for the YAML.** The spike uses one hand-written YAML file.
  Schema validation is a separate (large) workstream.
- **Variable capture (`{newUserId}` substitution).** The full DSL has this; the
  spike has one step and therefore no need.
- **A real reporting layer.** `Console.WriteLine` is the reporting layer for
  the spike. Don't add structure to it.
- **Error recovery beyond "throw with a useful message".** If the container
  doesn't start, let the exception propagate. The exception itself is the
  diagnostic for the spike's purpose.
- **Tests.** This is a throwaway experiment. Tests come later.
- **A CLI argument parser.** `args[0]` is the YAML path. No flags, no help text.
- **Cross-platform polish.** Whatever platform you're on is fine. Worrying about
  the others is a Phase 4 deliverable in the real project.
- **Multi-targeting.** The spike targets `net10.0` only. The architecture's
  multi-targeting story is for the real project.

## How to know when to stop

Two stop conditions, both equally valid.

**Stop one — it works.** You see PASS on the console with the observed 200 in
under five minutes from `dotnet run`. Write the paragraph (below). Put it down.
The next decision is which of the four project shapes you're going to commit to,
not how to extend the spike.

**Stop two — you're past two long evenings and still stuck.** Don't push
through. The fact that you got stuck *is* the experiment's result. Note where
you got stuck and what the symptom was. Write the paragraph anyway. The
information is real even if the binary outcome is "didn't work."

There is no third option where you keep going and add features. The spike
exists to produce a binary answer; adding scope is how solo founders accidentally
commit to projects they never decided to commit to.

## The paragraph

When you stop, regardless of outcome, write a single paragraph in `LEARNED.md`
covering:

- What happened (works / works partially / doesn't work).
- Roughly how long it took.
- Where, specifically, you got stuck if you did.
- One or two things that surprised you (positively or negatively).
- Anything .NET-10-specific that bit you, since that is one of the three things
  the spike was meant to discover.
- Your gut sense, in one sentence, about whether you want to keep building this.

The paragraph is for you. Save it. It is the input to a decision you will make
in four to eight weeks about which of four project shapes (VC-funded startup,
bootstrapped commercial open source, paid open-source side project, learning
project that gets put down) this actually is.

## How Claude should help on this codebase

Claude is being used as a pair programmer for this spike. Operating principles
for that role:

1. **Hold the scope boundary.** If the founder asks for a feature that's on the
   "do not do" list above, name it explicitly: "this is on the spike's
   don't-do list — do you want to revisit the scope, or stay focused on the
   slice?" Don't just implement it.
2. **Match the level of polish to the spike's purpose.** Don't refactor for
   abstraction. Don't extract interfaces. Don't add logging frameworks. Don't
   suggest tests. This is throwaway code by design.
3. **Aspire and Roslyn scripting are the two areas most likely to surface
   surprises on .NET 10.** When asked for help in either, prefer the simplest
   API surface that works, verify package versions against the current Aspire
   and Roslyn releases (do not invent API shapes from memory), and call out
   anything that looks like it depends on a preview feature rather than a GA
   one. If a package version on NuGet is in doubt, search rather than guess.
4. **If stuck, suggest stopping rather than working around.** A genuine block
   is information about the project's feasibility, and the founder gains more
   from naming it than from grinding through it.
5. **Don't write a README, a license file, or a CI config for the spike.**
   Those exist for projects intended to be shared. This one is for one person,
   for two evenings.

## What to do after the spike (a note, not an instruction)

The architecture documents describing the full vision live separately. They are
not the input to this spike; their input is, eventually, the spike's output.

If the spike works and the founder decides to continue:
- The next experiment is a second step type (probably `db-assert.postgres`
  against an Aspire-provisioned Postgres), which introduces the provider
  contract.
- Then the collectible load context, because memory leaks become a real
  problem once you run the script more than once.
- Then a hand-written JSON Schema for the YAML grammar covering the two step
  types.
- Then the third step type (`mq-expect.kafka`), which forces RETRY mode and
  the captured-variable mechanism.

But none of that is for this weekend. This weekend is one YAML, one container,
one verdict, one paragraph. Then stop.
