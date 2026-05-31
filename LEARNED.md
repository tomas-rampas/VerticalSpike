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

---

## Spike 2 — the provider contract (db-assert.postgres + fragment composition)

> **Verdict: it works.** Two providers, two services, two fragments composed into one Roslyn script, one overall `PASS` — end to end on .NET 10 / Aspire 13.3.5 / Npgsql 10.0.2.

### What happened

```
$ dotnet run -- examples/multi.yaml
[orders-up] http.rest -> PASS (status 200)
[db-up] db-assert.postgres -> PASS (scalar 1)
PASS
```
*(exit code 0; spike-1 regression `dotnet run -- examples/healthcheck.yaml` also still prints PASS)*

The platform now reads two services and two steps from YAML, provisions both via Aspire (httpbin container + Postgres with a named database), waits for both to be healthy, routes each step to its provider, composes the two `CsxFragment`s into one script, and runs it once via Roslyn. The provider contract holds in real code.

### Where it got sticky — three findings

1. **`PostgresDatabaseResource.GetConnectionStringAsync` is an explicit interface implementation.** The pre-task spec described it as a directly-callable instance method on the database resource. In practice, calling it on the concrete type produces `CS1061: does not contain a definition for 'GetConnectionStringAsync'`. The fix is a simple cast: `((IResourceWithConnectionString)db.Resource).GetConnectionStringAsync(ct)`. The XML docs confirm the method is on the interface, not overridden publicly on the concrete class.

2. **`using var` declarations inside a Roslyn script block are not supported (or cause parse errors).** The `db-assert.postgres` fragment originally emitted `using var __conn_... = new NpgsqlConnection(...)`. Roslyn rejected this with a parse error. The fix: drop `using`, allocate the connection and command as plain `var`, then call `.Dispose()` on them explicitly at the end of the block. Functionally equivalent for a one-shot script; the resource leak from skipping `using` in a CSX block is the same accepted cost as the assembly-load leak.

3. **`$$"""..."""` vs `$"""..."""` for CSX code generation is a genuine gotcha.** The fragment `Body` strings are C# code-generation templates: they contain variables like `{__pass_orders_up}` that must appear as literal braces in the emitted CSX, while step-name/service/path values must be interpolated at emit-time. A single-`$` raw string (`$"""`) treats `{{` as two interpolation openers (a compile error). A double-`$$` raw string (`$$"""`) treats `{{...}}` as an escaped literal brace and `{id}` as an interpolation hole — the right mental model for code generation. Step names (`orders-up`, `db-up`) contain hyphens; they had to be sanitized to `orders_up` / `db_up` before use as C# variable name suffixes.

### Things that worked exactly as expected

- `WaitForResourceHealthyAsync` on the Postgres database resource gated correctly — it did not return until the `appdb` database had been created by Aspire's lifecycle script. The transient `fail:` health-check log lines (Postgres "starting up", then "database does not exist") are expected noise from the internal health-check polling loop; by the time `WaitForResourceHealthyAsync` resolves, the database is genuinely ready.
- `Convert.ToInt64(scalar)` correctly handled the boxing ambiguity (Npgsql returns `int` boxed as `object` for `SELECT 1`; `Convert.ToInt64` accepted it without a cast mismatch).
- The two-shape YAML detection (presence of `\nservices:`) was the right minimal branch; no YAML versioning scheme needed.
- Roslyn fragment composition — union imports, union references, wrap each body in `{ }`, collect verdicts into `__results` — compiled and ran cleanly on the first attempt after the CSX body-generation issues above were resolved.

### Anything .NET-10 / Aspire-13 / Npgsql-10 specific

- No new .NET 10 or Roslyn 5.3.0 surprises beyond spike 1. The `$$"""` code-generation pattern is a C# 11 language feature that works cleanly on .NET 10.
- Aspire 13.3.5 added a health check for `PostgresDatabaseResource` (the `appdb_check`) that runs in addition to the server-level `pg_check`. Both must pass before `WaitForResourceHealthyAsync` resolves — which is the correct behavior and exactly what the spike relied on.
- Npgsql 10.0.2 paired with Aspire 13.3.5's bundled Postgres 17.6 image worked without any version negotiation friction.

### Gut sense

> The provider contract is not only documentable — it works in real code, the fragment composition seam is clean, and the two-provider case exposed exactly the kind of friction (CSX code-generation escaping, explicit-interface connection strings) that a design document would have smoothed over. I want to build the next one.

### Post-spike fix: log noise and a latent wait race

The `fail:` health-check stack traces (`NpgsqlException: Exception while reading from stream`, `PostgresException: 3D000: database "appdb" does not exist`) that flooded the console during a passing run were cosmetic transient startup spam — Aspire's internal health-check poller logging at Error level while Postgres was still booting, before its own lifecycle script had created the database. They resolved on their own before `WaitForResourceHealthyAsync` returned, but made the run look broken. Silenced by calling `builder.Services.AddLogging(lb => { lb.AddFilter(null, LogLevel.Warning); lb.AddFilter("Microsoft.Extensions.Diagnostics.HealthChecks", LogLevel.None); })` before `builder.Build()` — requiring explicit `using Microsoft.Extensions.DependencyInjection` and `using Microsoft.Extensions.Logging` since neither is in the Aspire AppHost SDK's implicit usings.

The noise was also masking a real race: the original wait loop called `WaitForResourceHealthyAsync(svc.Name)` for postgres, which targets the SERVER resource ("pg") — healthy once the server accepts connections, but before Aspire's lifecycle script creates the database. The run passed only because Roslyn compilation took long enough to let `appdb` get created during the gap; on a faster machine it could intermittently FAIL. Fixed by waiting on `pgDbBuilders[svc.Name].Resource.Name` (the DATABASE resource, e.g. "appdb"), which Aspire marks healthy only after both server and database exist.

---

## Spike 3 — collectible AssemblyLoadContext under load

### Sitting one (of three): the unloadable execution path

> **Interim verdict: the execution-path replacement works and a single run unloads cleanly.** This is collectibility-in-principle, *not* yet the memory-model-under-load answer — that is sittings two (harness + control arm) and three (run + interpret).

**What happened.** Replaced the spike-1/2 run-path (`CSharpScript.Create<bool>(...).RunAsync(globals)`) with a compile-to-collectible-context path: wrap the composed body in a generated `public static class GeneratedTest { static async Task<bool> RunAsync(Globals g) {...} }`, build a `CSharpCompilation`, `Emit` to a byte[], `LoadFromStream` those bytes into a collectible `AssemblyLoadContext`, get the method as a `Func<Globals,Task<bool>>` delegate, and invoke. Both examples still pass through the new path:

```
$ dotnet run -- examples/multi.yaml
[orders-up] http.rest -> PASS (status 200)
[db-up] db-assert.postgres -> PASS (scalar 1)
[smoke] collectible ALC unloaded after run (weak ref dead)
PASS

$ dotnet run -- examples/healthcheck.yaml   # spike-1 regression
[healthcheck] http.rest -> PASS (status 200)
[smoke] collectible ALC unloaded after run (weak ref dead)
PASS
```

**The framing finding (came before any code).** `CSharpScript.RunAsync` loads its submission through Roslyn's internal `InteractiveAssemblyLoader` into the **Default** load context, and the scripting API exposes no hook to load into a custom one. So the architecture's compile-once / collectible-context model is fundamentally incompatible with the scripting run-path — spike 3 *forces* the move to the raw `CSharpCompilation` → `Emit` → custom-ALC pattern. This is itself the first result, independent of any memory number.

**The regression-protecting trick.** The two provider `Emit` templates and the `Globals` class are **unchanged, byte-for-byte**. The generated wrapper hoists the host boundary into locals at the top of the method — `var Http = g.Http; var BaseUrls = g.BaseUrls; var ConnectionStrings = g.ConnectionStrings;` — so the spliced fragment bodies see exactly the bare identifiers they saw as script globals. Preserving the templates is what kept the spike-2 regression diff to zero on the provider side.

**The smoke test deliberately uses the sitting-two boundary.** Unload is initiated inside a `[MethodImpl(MethodImplOptions.NoInlining)]` method that creates the ALC, loads, invokes, drops the delegate, calls `Unload()`, and returns *only* a `WeakReference(alc, trackResurrection: true)`. The caller then runs the prescribed bounded GC loop — `GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true)` + `GC.WaitForPendingFinalizers()`, up to 10 iterations — and checks `IsAlive`. `Unload()` lives inside the boundary on purpose: it needs the strong ALC handle, and that handle must not escape into the caller frame or it would root the context and the weak ref would never die. An inlinable shortcut here would have given a green result that didn't predict sitting two; this path is the one sitting two will reuse verbatim.

**Where it could have got sticky (but didn't, this time).** The metadata-reference closure. The scripting API had been supplying the framework reference set implicitly; a bare `CSharpCompilation` makes you own it. Built it from `AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")` (split on `Path.PathSeparator`, `.dll` only, deduped) unioned with the fragment-declared references, the host assembly (for `Globals`), and Npgsql. It compiled with zero warnings on the first run — but this is the part that would bite first if a future provider pulls in a type whose assembly isn't on the TPA list (`CS0012`).

**Toolchain specifics worth remembering.**
- Roslyn 5.3.0's *compiler* API (not scripting) on .NET 10 needs no `LanguageVersion` coaxing; default parse options compiled the generated `async`/tuple/LINQ body fine.
- Compiled the generated assembly at `OptimizationLevel.Release` so the smoke test is conservative — the JIT is freer to enregister/extend local lifetimes under Release, so a clean unload there is a stronger signal than a Debug build would give.
- A `[MethodImpl]` attribute on a `static` local function inside top-level statements is accepted, and a local function may be declared after the file's `return` (declarations aren't unreachable code) — which let the release boundary live at the bottom of the top-level section.
- Returning `null` from `AssemblyLoadContext.Load` (resolve-up-to-Default) means the collectible context holds only the thin generated glue; all the statically-stateful provider code (HttpClient, Npgsql) stays in the Default context. That is the structural reason a clean unload was plausible — and why a *single*-run unload says little about the *under-load* question.

**What this does and does not establish.** Establishes: the unloadable path runs the real two-provider closure to the same verdicts, and one ALC unloads after one run. Does **not** establish: that the post-unload floor stays flat across many cycles, that Npgsql's pool / any default-context static plateaus rather than creeps, or that process RSS / handle count / loaded-assembly count return to baseline at scale. Those are the actual spike-3 question. **Status: ready for the measurement harness and the empty-body control arm (sitting two).**
