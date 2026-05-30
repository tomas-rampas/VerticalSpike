# LEARNED.md

**Outcome: it works.** `dotnet run -- examples/healthcheck.yaml` reads the YAML, provisions
the `kennethreitz/httpbin` container through Aspire's programmatic builder, waits for it to
report healthy, generates a small CSX step, compiles and runs it once through Roslyn, and
prints `observed status = 200` / `PASS` with exit code 0 — the whole vertical slice, end to
end, on .NET 10. It came together quickly: the code is a single ~90-line `Program.cs`, and
almost all of the actual effort went into two Aspire discoveries rather than into writing
code. The first and most important is that a bare `DistributedApplication.CreateBuilder()` +
`StartAsync()` does **not** "just work" under plain `dotnet run` — it throws in `BeforeStart`
because the Aspire dashboard demands `ASPNETCORE_URLS` and `ASPIRE_DASHBOARD_OTLP_*`
environment variables that the `aspire run` tooling normally injects; a headless AppHost has
to opt out with `new DistributedApplicationOptions { DisableDashboard = true }`. The second:
the architecture's assumed endpoint API, `app.GetEndpoint(name, "http")`, doesn't exist in
Aspire 13.x — the real path is to keep the `IResourceBuilder` returned by `AddContainer(...)`
and call `.GetEndpoint("http").Url` after start. Pleasant surprises outnumbered the nasty
ones: Roslyn scripting on .NET 10 / C# 14 (Roslyn 5.3.0) needed **no** `LanguageVersion`
workaround despite the warning in the setup notes, and `WaitForResourceHealthyAsync` returned
the instant the container reached `Running`, exactly as the docs promise for a resource with
no health check — no hang, and no race in practice with httpbin. The .NET-10-specific shape
worth remembering is that Aspire 13.x ships as an MSBuild SDK
(`<Project Sdk="Aspire.AppHost.Sdk/13.3.5">`) that encapsulates the old `Aspire.Hosting.AppHost`
package, pulls in the DCP orchestrator, and supplies a global `using Aspire.Hosting`;
everything pinned and restored cleanly against `net10.0` on SDK 10.0.108 GA with Docker 28.4.0.
Gut sense, one sentence: both surprises were the cheap, discoverable, API-doesn't-match-the-doc
kind rather than anything structural, and watching one YAML file turn into a real provisioned
container and a real PASS/FAIL verdict felt enough like a usable test platform that I want to
keep building it.
