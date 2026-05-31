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

### Sitting two (of three): the measurement harness

> **Interim status: the instrument is built and validated; the memory-model verdict is NOT in yet.** Sitting two delivers the harness, not the answer. The number-and-interpretation that spike 3 exists to produce comes from sitting three's full run. Nothing below should be read as "the memory model works."

**What happened.** Built a `--measure` two-arm measurement harness on the sitting-one path (all in `Program.cs`; no new files). Folded `RunOnceAndUnload` into `RunBatchAndUnload(…, n, …)` so the smoke test (`n = 1`) and every measurement cycle (`n = batch size`) share *identical* unload mechanics. Each cycle creates a fresh collectible ALC, loads the emit-once assembly bytes, invokes `n` times, drops the delegate, `Unload()`s inside the `NoInlining` boundary, then the caller runs the bounded compacting-GC loop and captures a metric vector (managed floor, GC heap/committed/fragmented, private bytes, working set, handle count, loaded-assembly count, gen0/1/2, unload flag, GC-iters-to-unload, batch ms, all-pass) to a per-cycle CSV. A headline OLS regresses the post-warmup floor vs cycle for each arm and reports the real−control slope difference. The default verdict+smoke path is unchanged; both `examples/*.yaml` regressions still print PASS, builds clean with zero warnings.

**The control arm.** A second assembly with the same delegate / ALC / async-state-machine shape but no provider I/O (`await Task.CompletedTask; return true;`), so the real−control slope difference attributes any leak to the provider closure rather than to the ALC mechanics. Run control-first, then real, in one process.

**Mechanics validated — these are findings about the *instrument*, not about memory.** A small verification run (8 cycles, `n = 50`) confirmed the harness behaves: `unloaded = true` on every cycle of both arms, `gc_iters_to_unload = 2` consistently, `loaded_assemblies` flat (~103, no +1/cycle creep), handle count flat, `all_pass = true` every cycle, and `batch_millis` cleanly separating control (2–23 ms) from real (500–1837 ms, i.e. the real arm genuinely does the HTTP+DB round-trips). The `NoInlining` boundary that worked once in sitting one keeps working under repeated create/unload.

**Why no memory verdict yet (and a caution against the obvious misread).** The verification run is far too small to interpret — six post-warmup points, enormous slope CIs. Its headline even showed a large positive `private_bytes` slope (~364 KB/cycle on the control arm), which is **not** a leak: the raw CSV shows private bytes going 191→193 MB and *plateauing*, and OLS over that short warmup ramp manufactures a slope. This is precisely the failure-mode-D (warmup-mistaken-for-leak) the design anticipated, and exactly why the protocol drops the first 5 cycles and needs M = 50 points before any slope is trustworthy. Reading the smoke run as evidence either way would be the confirmation trap this spike is meant to avoid.

**Design/toolchain notes worth keeping.**
- The build runs **workstation + concurrent GC** (verified: no `System.GC.*` keys in `runtimeconfig.json`; the CSV metadata header records `isServerGC=False`). No `.csproj` GC pinning was needed — the forced blocking compacting collects make the floor reads deterministic enough, and workstation GC keeps `private_bytes` free of server-GC per-core-heap inflation.
- Per-step provider `Console.WriteLine` is silenced (`Console.SetOut(TextWriter.Null)`) for the duration of the loop and restored for the headline — at 50×200 it would otherwise emit ~20k lines and pollute `batch_millis`.
- Arms run as separate phases (control first), not interleaved: floor slope is offset-invariant, so phase order doesn't bias the comparison, and per-arm regression stays clean.
- The N-sweep ladder (1/10/100/1k/10k) was deferred to sitting three rather than built now.

**Status: harness ready and self-validated. Sitting three is the real run (M = 50, N = 200, plus the ladder), the honest interpretation, and the verdict entry — including the case where the numbers are bad.**

### Sitting three (of three): the run and the verdict

> **Verdict: PASS — the compile-once / collectible-`AssemblyLoadContext` memory model returns process memory to baseline under load against the real two-provider closure.** The architecture's single largest remaining technical risk is retired. The numbers were *not* bad — but reading them honestly took more care than "is the slope zero," and two of the figures look alarming until you look at the shape behind them.

**Pre-registered before the data landed** (so the read is not post-hoc): pass = managed-floor slope **< 100 bytes/cycle** (derived from a heavy adopter ≈ 100k ALC recycles/day, a 30-day uptime SLO, and a 256 MiB ALC-overhead budget), with a CI that includes zero counting as the strongest pass. Hard gates, applied regardless of the floor slope: 100% unload success, flat `loaded_assemblies`, flat `handle_count`, plateauing `private_bytes`. Ladder (N-sweep) triggered only by a positive slope ≥ T/3; skipped if the floor is clean.

**What ran.** `dotnet run -- examples/multi.yaml --measure --cycles 50 --n 200 --warmup 5`, two arms (empty-body control first, then the real `http.rest` + `db-assert.postgres` closure), then a replication at `--warmup 30` to regress only the settled tail (cycles 30–49). ~10,000 real HTTP+DB round-trips per run; both runs on workstation+concurrent GC.

**The hard gates passed cleanly, on every one of 200 measured cycles across both runs:**
- **Unload success 100%** (50/50 each arm, each run); `gc_iters_to_unload = 2` *every single cycle*. The collectible context releases within two forced gen-2 collections, every time, with no drift.
- **`loaded_assemblies` slope 0.000** — control flat at 103, real flat at 104. The per-cycle generated assembly always unloads; the real arm's constant +1 is one dependency loaded *once* into the Default context and never growing. This is the definitive ALC-leak gate and it is unambiguous.
- **`handle_count`** flat/declining (real even *dropped* ~88 handles at one early cycle as pooled sockets consolidated), never climbing — no native-handle leak.
- **`private_bytes`** settles then plateaus (real 190 MB → 186.5 MB, then rock-stable); never climbs. **`all_pass = true`** every cycle — the real closure did genuine work and passed all 10,000 executions, with no batch-time degradation that would signal pool exhaustion.

**The managed floor does not grow — but the slope alone would mislead you twice.**
- *The real arm's floor settles downward, not flat.* At `--warmup 5` the real slope is **−2,300 ± 250 B/cycle (R²=0.66)** — negative, and statistically significant. Not a leak: the floor stair-steps *down* (19.40 → 18.75 → 18.67 → 18.62 → 18.575 MB) as a warmup tail that runs ~25 cycles, far longer than the 5 dropped, then holds dead-flat (cycles 31–49 span ~1.7 KB total). Regressing only the settled tail (`--warmup 30`, cycles 30–49) gives the real steady-state slope: **+82.8 ± 81.7 B/cycle, R²=0.054 — CI includes zero, under the 100 B/cycle threshold.** That is the headline number, and it is a clean pass.
- *The control arm fakes a catastrophic slope from a one-time trim.* In the replication the control floor was identical to the byte for cycles 25–42 (19,382,848), dropped once by ~620 KB at cycle 43 (a GC committed-memory trim), then was flat again — yet OLS over that 20-cycle window reported **−44,901 ± 6,516 B/cycle (R²=0.73)**. A single step over a short window looks like a steep, high-R² trend to a linear fit. Wrong sign for a leak anyway, but the lesson stands: **over short windows, shape beats slope.**

**A method finding worth recording: the real−control subtraction is fragile.** When a control-side GC trim lands inside the window it produced a spurious "+44,984 B/cycle provider-attributed" figure — pure artifact. The robust signals are each arm's own *shape* plus the hard gates, not the subtraction. The control arm still earned its keep: it confirmed the ALC mechanics themselves don't grow the floor (flat between trims, 100% unload, flat assembly count). It just can't be trusted as a byte-exact baseline to subtract.

**Why this result was structurally likely (and what it does *not* clear).** The collectible context holds only the thin generated glue; `HttpClient` (host-owned, shared) and `Npgsql` (fixed connection string → one bounded pool) live in the Default context and are never pinned by the ALC. So the named scary suspects from the spike brief were bounded *by design* in this closure. That means this PASS validates the **shared-client / fixed-connection-string** path — **not** the `new HttpClient()`-per-execution anti-pattern, which this example never exercises. A future spike that wants to retire *that* risk must move client creation into the script body.

**Ladder: not run.** Pre-registered rule — skip when the floor is clean. The real steady-state CI includes zero; there is no positive leak to localise, so the N-sweep would only measure noise at different batch sizes. Deliberately not run.

**Toolchain notes.** `GC.GetTotalMemory(forceFullCollection: true)` occasionally returns a single-cycle +98 KB blip (a transient not-yet-collected object surviving the forced collect) — sub-0.5% jitter, harmless. Workstation-GC committed-memory trims occur at unpredictable cycles and are the main source of large apparent short-window slopes; they are benign consolidation, not leaks. Raw data committed with this entry as the evidence behind the verdict: `spike3-memory.csv` (warmup=5 full window) and `spike3-memory-steadystate.csv` (warmup=30 settled-tail replication).

**What this unlocks.** The memory model is buildable, not merely defensible — the project moves from "the design is defensible" to "the design is buildable," which per CLAUDE.md is the moment to make the four-shapes decision against real evidence. That decision is the founder's, not this spike's. Spike 3 is complete: question answered (yes), prior regressions still green, verdict recorded.
