# CLAUDE.md

## What this project is

This is the **VerticalSpike** repository — a sequence of small, time-boxed
spikes that progressively validate the architecture of a YAML-driven
integration-testing platform for .NET microservices. The full vision is much
larger (see the three architecture documents in `/docs` if present); this
repository contains the working code that converts that architecture from
design intent into evidence, one spike at a time, in a single accumulating
project rather than as throwaway artifacts per spike.

The repository has the unusual property of being **both research code and
the seed of a real platform**. It is research code in the sense that each
spike has a specific question to answer and is allowed to take shortcuts that
production code would not; it is the seed of a real platform in the sense
that successful patterns are kept and extended rather than thrown away. This
hybrid posture is deliberate, but it requires discipline to maintain — see
"How to keep the project honest" below.

## What the spikes have established so far

| Spike   | What it covered                                                                                                                                              | Verdict  |
|---------|--------------------------------------------------------------------------------------------------------------------------------------------------------------|----------|
| Spike 1 | End-to-end vertical slice: one YAML → one container → one Roslyn-compiled step → one verdict, on .NET 10 / Aspire 13.x.                                      | PASS     |
| Spike 2 | Provider contract and fragment composition: two providers (`http.rest` and `db-assert.postgres`) composed into one Roslyn script, on .NET 10 / Aspire 13.3.5 / Npgsql 10.0.2. | PASS     |
| Spike 3 | Collectible AssemblyLoadContext under repeated iteration against the real provider closure — does memory return to baseline across thousands of iterations? | upcoming |

The `LEARNED.md` file in this repo carries one paragraph per spike summarising
what was discovered. Always append; do not edit prior spikes' entries. The
LEARNED log is the project's memory and is more valuable than any individual
piece of code in the repository.

## What "done" looks like for a spike

A new spike is complete when **all three** of the following hold:

1. The spike's specific question has a clear yes-or-no answer demonstrated
   by code that runs end-to-end.
2. A regression of every prior spike still passes (the existing
   `examples/*.yaml` files still produce their original verdicts when re-run).
3. A new `LEARNED.md` entry has been appended naming what happened, where
   it got sticky, what surprised you, and any toolchain-specific findings
   that will matter for future spikes.

A spike that breaks an earlier spike's regression is not done; either the
regression is restored or the change is reverted. The accumulating-project
discipline is what protects you from the alternative, which is a graveyard
of half-finished experiments that no longer compile together.

## The upcoming spike: collectible AssemblyLoadContext under load

**The question.** When the same compiled script is executed many thousands
of times inside a collectible `AssemblyLoadContext`, and the context is
unloaded between groups, does process memory return to baseline? The
architecture's compile-once memory model (Doc 1, §5) depends on this. The
risk is **static state in transitive dependencies**: `HttpClient`'s default
handler pool, `Npgsql`'s connection pool, `Confluent.Kafka`'s producer cache,
OpenTelemetry tracers, logging-provider registrations — any of these can
pin objects across the context boundary and produce slow memory growth that
only manifests at scale.

**The shape of the experiment.** Take the existing two-provider example
(`examples/multi.yaml`) and put the run-the-script step inside a loop. After
each batch of N iterations, unload the context, force a full GC, and record
managed-heap size. Plot the curve. The desired result is a sawtooth that
returns to a stable baseline; the failure mode is a sawtooth on a rising
trendline, or no sawtooth at all because the unload never released
anything. The numbers matter more than the code's elegance.

**Suggested measurement protocol.** Iterate at 1, 10, 100, 1000, and 10000
script executions, unloading the load context and forcing
`GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true)`
followed by `GC.WaitForPendingFinalizers()` between groups. Record
`GC.GetTotalMemory(forceFullCollection: true)` at each checkpoint. A
load-context that has fully unloaded reports its `IsCollectible` weakly
referenced target as null after a generation-2 collection; check this
explicitly. If the weak reference is still alive after GC, the context did
not unload, and the experiment's answer is "no, it didn't release."

**Time-box: two long evenings, or four shorter ones.** The deliverable is
**a number and an interpretation**, not a feature. If after the time-box
the memory does not stabilise, the LEARNED.md entry records what is keeping
it alive (heap dumps, dotMemory, `dotnet-counters`, or just the obvious
suspects from the transitive closure). That is genuinely useful information
even if the answer is "the architecture's memory model needs revision."

**What success unlocks.** If memory returns to baseline cleanly, the
architecture's single largest remaining technical risk is retired. The
project moves from "the design is defensible" to "the design is buildable."
That is the moment to step back from spike work and decide what the
project actually is — the four-shapes decision (VC-funded startup,
bootstrapped commercial open source, paid open-source side project, or
learning project to put down) is then made against real evidence.

## How to keep the project honest

The two failure modes I would worry about for this repository, both
characteristic of solo founders without external pressure:

**Drift toward "the real project" without deciding to start it.** Every
spike adds working code, and at some point the codebase looks enough like
a real product that you slip into product mode — refactoring for cleanliness,
adding tests for coverage, writing a README for adopters — without ever
making the decision to commit to building the product. Resist this by
asking, before any non-spike work, "is this in service of the current
spike's question, or am I drifting into product mode?" If it is the latter,
note it on the to-do list for after the four-shapes decision and stop.

**Drift away from the LEARNED discipline.** It is much more tempting to
write code than to write the paragraph that captures what the code taught
you. The paragraph is the more valuable artifact. If you find yourself
finishing a spike without writing the LEARNED entry, the spike is not
finished; do the entry first, then move on.

A small operational rule: keep each spike's YAML example file in
`examples/` named for what it demonstrates (`healthcheck.yaml`,
`multi.yaml`, `memory-stress.yaml`, etc.). Never delete a previous spike's
example; the regression-still-passes discipline depends on them.

## Stack and conventions

- **.NET 10.** The project targets .NET 10 because that is the runtime the
  full platform will ship on, and the spikes are the cheapest moment to find
  toolchain surprises early. Use the current GA build.
- **Aspire 13.x.** Pinned in the `.csproj` to a specific version; the
  AppHost is constructed programmatically through
  `DistributedApplication.CreateBuilder(new DistributedApplicationOptions
  { DisableDashboard = true })`. Endpoints resolve through the
  `IResourceBuilder` returned by `AddContainer`/`AddProject`/`AddPostgres`,
  via `builder.GetEndpoint("http").Url`, not through a non-existent
  `app.GetEndpoint(name, scheme)`.
- **Roslyn scripting** (`Microsoft.CodeAnalysis.CSharp.Scripting`).
  `CSharpScript.Create<bool>()` + `RunAsync` for now. The collectible
  load-context pattern is the subject of spike 3; until that spike
  succeeds, treat the assembly-leak cost as accepted.
- **YamlDotNet** for parsing.
- **Docker Desktop or Podman** as the container runtime.
- **Health-check log noise is suppressed.** The runner configures
  `Microsoft.Extensions.Diagnostics.HealthChecks` to `LogLevel.None` and
  the global default to `LogLevel.Warning`, because Aspire's internal
  health-check pollers emit transient `Error` lines while resources are
  still booting. These are cosmetic; suppressing them is the right
  operational default for a headless test runner.
- **Wait on the resource the test depends on, not the server above it.**
  For a Postgres dependency, wait on the database resource (the named
  database, `appdb`), not the server resource (`postgres`). The server
  becomes healthy when it accepts connections; the database is created
  later by Aspire's lifecycle script, and waiting on the server lets
  queries race the database's creation. This is a known landmine
  documented in Doc 1, §4.3.

## CSX emit-template conventions (from spike 2, mandatory for all providers)

These are non-negotiable rules every step provider's emit code must
follow. They are also recorded in Doc 1, §13.3.1; this section is the
fast-reference version.

1. **No `using var` in emitted CSX.** Roslyn's script parser rejects the
   C# 8 declaration form even when the host language version supports it.
   Emit plain `var` declarations and call `.Dispose()` in a `try/finally`
   at the end of the block.

2. **Sanitise step ids before splicing them into variable names.** YAML
   step ids may contain hyphens (`orders-up`); C# identifiers may not.
   Use `CsxFragment.SanitiseId(stepId)` (replaces `-` with `_`) before
   using the id as a variable-name suffix.

3. **Emit code templates use `$$"""..."""` raw strings, not `$"""..."""`.**
   With double dollars, `{{ ... }}` denotes literal braces in the emitted
   code (the script's own block delimiters) and `{id}` denotes an
   interpolation hole the emitter fills. The single-dollar form inverts
   these meanings and fails as soon as the body contains any C# block,
   which it always does.

4. **Connection strings on Aspire managed resources require the cast.**
   `GetConnectionStringAsync` is implemented explicitly on
   `IResourceWithConnectionString`, not on the concrete resource type.
   Use `((IResourceWithConnectionString)resource.Resource).GetConnectionStringAsync(ct)`.

5. **Each step's body is wrapped in its own `{ }` brace scope.** Locals
   declared inside one provider's body must not be visible to another's.
   This is what the brace scope guarantees and is what makes fragment
   composition tractable.

## Things to deliberately not do (yet)

The CLAUDE.md from spike 1 had a long do-not-do list because the spike was
specifically scoped against scope-creep. Most of those items have since
become things spike 2 or 3 will (correctly) tackle. The current do-not-do
list is shorter and tracks what is *still* premature:

- **A real reporting layer.** Console output is enough for the spike phase.
  The five-layer reporting architecture in Doc 1 §14 belongs to the real
  project.
- **A JSON Schema for the YAML.** Validating by hand is fine while there
  are three step types. Schema work begins when there are five or six.
- **Tests for the spike code.** The example YAML files are the tests.
  Each spike's regression-still-passes check is the integration test.
- **A README intended for external readers.** This repository is for the
  founder, not for adopters. When the four-shapes decision lands and the
  project shape becomes clear, the README is rewritten for the relevant
  audience.
- **CI configuration.** The same logic.
- **Cross-platform polish.** Whatever platform you're on is fine.

## How Claude should help on this codebase

Claude is being used as a pair programmer for an ongoing solo-founder
research project. The operating principles:

1. **Hold the spike's scope.** When asked for help, first establish which
   spike's question the current change serves. If a request is in service
   of a future spike or of "the real project," name that explicitly: "this
   is a future-spike concern — do you want to scope it to the current
   spike, or to capture it as a follow-up?"

2. **Protect the regression discipline.** Any change to shared code (the
   YAML parser, the Aspire host construction, the script execution loop)
   should be checked against the existing `examples/*.yaml` files. Suggest
   running them after non-trivial edits.

3. **Match polish to purpose.** Refactor only when refactoring is the
   point of the spike (e.g., spike 3 may want to extract the iteration
   loop). Otherwise, working code is better than elegant code, and the
   LEARNED entry is more valuable than the architectural abstraction.

4. **Treat `LEARNED.md` as a first-class artifact.** When a spike's work
   is reaching a stopping point, prompt for the LEARNED entry before the
   next spike starts. If the founder skips the entry, name the omission
   gently rather than letting it pass.

5. **Verify Aspire / Roslyn API surface against current releases, not
   memory.** Both libraries' surfaces have moved between recent versions
   (this is documented in the LEARNED log). When in doubt, search the
   current package version rather than invent an API shape that "should"
   work.

6. **Suggest stopping when stuck.** A spike that hits a genuine block
   produces useful information; pushing past the time-box produces
   diminishing returns and increasing scope-drift.

7. **Update the architecture documents when spikes correct them.** When
   a spike discovers a documentation-vs-reality drift (as spikes 1 and 2
   both did), suggest folding the correction into the relevant section of
   Doc 1 alongside the LEARNED entry. The architecture documents are
   meant to track reality.

## Where the architecture documents fit

The three documents — Doc 1 (Technical Architecture), Doc 2 (DSL
Specification), Doc 3 (MVP Project Plan) — are the design background for
the project, not its build plan. The build plan is "run the next spike,
learn from it, decide what to do next." The 36-page MVP plan in Doc 3
describes the well-funded seven-person version of the project; the version
you are actually building is a sequence of weekend-sized experiments that
compound into something the four-shapes decision will eventually shape.

When a spike's findings disagree with the architecture documents, the
spike is right and the documents need updating. Doc 1's "validation log"
(§1.2) is the table that records this. Always update the documents and
the LEARNED log together; never let them drift.
