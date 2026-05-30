# LEARNED.md

> **Verdict: it works.** One YAML → one container → one Roslyn-compiled step → one `PASS`, end to end on .NET 10.

## What happened

A single command runs the whole vertical slice:

```
$ dotnet run -- examples/healthcheck.yaml
observed status = 200
PASS
```
*(exit code 0)*

It reads the YAML, provisions the `kennethreitz/httpbin` container through Aspire's
programmatic builder, waits for it to report healthy, generates a small CSX step, then
compiles and runs it once through Roslyn and prints the verdict.

**How long:** it came together quickly — the code is a single ~90-line `Program.cs`, and
almost all of the effort went into two Aspire discoveries rather than into writing code.

## Where it got sticky — two Aspire findings

1. **A headless AppHost must opt out of the dashboard.** A bare
   `DistributedApplication.CreateBuilder()` + `StartAsync()` does **not** "just work" under
   plain `dotnet run` — it throws in `BeforeStart` because the Aspire dashboard demands
   `ASPNETCORE_URLS` and `ASPIRE_DASHBOARD_OTLP_*` environment variables that the
   `aspire run` tooling normally injects.
   **Fix:** `new DistributedApplicationOptions { DisableDashboard = true }`.

2. **The assumed endpoint API doesn't exist.** `app.GetEndpoint(name, "http")` — what the
   architecture assumed — isn't part of Aspire 13.x.
   **Real path:** keep the `IResourceBuilder` returned by `AddContainer(...)`, then call
   `.GetEndpoint("http").Url` after start.

## Pleasant surprises

- **Roslyn scripting needed no `LanguageVersion` workaround.** On .NET 10 / C# 14
  (Roslyn 5.3.0) it just worked — despite the warning in the setup notes.
- **`WaitForResourceHealthyAsync` behaved exactly as documented.** It returned the instant
  the container reached `Running` (the documented behavior for a resource with no health
  check) — no hang, and no race in practice with httpbin.

## .NET 10 specifics worth remembering

- Aspire 13.x ships as an **MSBuild SDK** — `<Project Sdk="Aspire.AppHost.Sdk/13.3.5">` —
  that encapsulates the old `Aspire.Hosting.AppHost` package, pulls in the DCP orchestrator,
  and supplies a global `using Aspire.Hosting`.
- Everything pinned and restored cleanly against `net10.0` on **SDK 10.0.108 GA**, with
  **Docker 28.4.0**.

## Gut sense

> Both surprises were the cheap, discoverable, API-doesn't-match-the-doc kind rather than
> anything structural — and watching one YAML file turn into a real provisioned container and
> a real PASS/FAIL verdict felt enough like a usable test platform that I want to keep
> building it.
