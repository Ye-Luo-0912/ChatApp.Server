# AGENTS.md — ChatApp Server

Guidance for humans and coding agents working in this repository.

## Architecture boundaries

| Layer | Path | May depend on | Must not |
|---|---|---|---|
| Core | `Core/` | BCL only | ASP.NET Core, EF Core, Redis, NLog, Infrastructure |
| Infrastructure | `Infrastructure/` | Core, EF Core, Redis/Garnet, Realtime integration | Controllers or HTTP response types |
| Host/API | `Controllers/`, `Middlewares/`, `Filters/`, `Program.cs` | Core and Infrastructure | Persistence details in controllers |
| Tests | `tests/` | Public behavior and explicitly exposed internals | Depend on execution order or shared mutable fixtures |

Dependency direction is **Host → Infrastructure → Core**. Realtime contracts and integration code come from the pinned sibling repository `../ChatApp.RealtimeServices`; keep `Realtime.version`, project references, CI checkout, and Docker build context aligned.

## Engineering priorities

In descending order:

1. Correctness and data safety.
2. Stable failure and recovery behavior.
3. Measured latency, throughput, allocation, and resource use.
4. Simple code with a small maintenance surface.

Do not trade correctness for a benchmark. Do not add a cache, retry, lock, queue, background worker, abstraction, or service boundary without defining its failure semantics.

## Reuse, performance, and maintainability

These are mandatory development principles for every change:

- Reuse an existing focused component, contract, validation rule, or test helper before adding a parallel implementation. Extract shared behavior only when there are at least two real callers and its ownership is clear.
- Treat performance as a product requirement: keep request and worker hot paths allocation-aware, avoid unnecessary network round trips, database queries, serialization, and object hydration, and optimize only after measurement.
- Keep code maintainable: preserve layer boundaries, use small cohesive components and explicit failure semantics, remove superseded paths, and cover behavior with focused tests. Prefer a direct, readable implementation over speculative generality.
- When reuse, performance, and maintainability conflict, retain correctness and data safety first, then select the smallest measured design with the lowest long-term maintenance cost.

## Simplicity and code growth

- Prefer deleting obsolete paths and consolidating duplicate behavior over adding adapters around adapters.
- Add an abstraction only for a real architectural boundary or repeated behavior. One speculative caller is not enough.
- Keep hot-path methods direct. Avoid reflection, repeated JSON parsing, entity hydration, hidden network calls, and per-request high-cardinality objects.
- Extend an existing focused component before creating another near-duplicate component.
- Do not grow controllers, middleware, interceptors, or repositories into orchestration containers. Extract only cohesive behavior.
- New code should normally replace at least as much accidental complexity as it introduces.

## Cache and distributed-state invariants

- Consumers must depend on the focused value-store, atomic-state, or set-index interface that matches their needs. Do not add a broad compatibility facade.
- Generic cache reads must not acquire distributed locks or invoke hidden value factories. Cache-aside orchestration belongs to the owning service, where timeout and fallback behavior are explicit.
- Do not expose a generic distributed-lock helper. Use a business-specific lease or one atomic Redis operation when ownership is actually required.
- Never automatically retry an operation whose completion may be ambiguous, including `SET NX`, `INCR`, `GETDEL`, lock acquisition, CAS consume, or a multi-key transaction.
- StackExchange.Redis owns connection recovery. Any additional retry must be limited to a proven idempotent/read-only operation and justified by a test or measurement.
- Prefer one Redis round trip: Redis STRING for hot payloads, pipelined/batched reads for collections, and short Lua scripts for atomic multi-key decisions.
- Redis is not the source of truth for durable business data. PostgreSQL plus Outbox remains the durability boundary.
- Do not place raw access/refresh tokens, passwords, verification codes, or other secrets in cache keys or logs.

## Database and Outbox invariants

- Project DTOs in SQL; do not `Include` and materialize full entities for read-only API responses.
- Updates must be narrow or concurrency-protected. Do not call `Update` on an already tracked aggregate just to persist changes.
- Do not enable provider-wide automatic retries around transactions or non-idempotent writes. Retry only an explicitly idempotent unit with tested completion semantics.
- Outbox claiming and completion must validate lease ownership. Delivery must be idempotent at the database constraint/`ON CONFLICT` boundary.
- A duplicate in one batch must never cause unrelated rows to be marked delivered.

## Runtime and deployment

- Keep the modular monolith. Scale by deployment role before creating more services:
  - API: HTTP request path.
  - Worker: email, notifications, exports, scans, cleanup, and other background work.
  - Realtime: the existing sibling runtime.
- Production containers are read-only except for explicitly mounted data directories.
- Production logs must always reach stdout or OTLP. File logging is optional and requires an explicit writable mount.
- Reverse-proxy, Kestrel, endpoint body limits, and timeouts must agree.

## Performance work

- Establish a Release baseline before and after a performance change.
- Record at least p50/p95/p99, throughput, error rate, allocation/request, GC pause, CPU, database-pool wait, query count, and Redis RTT.
- Short smoke tests validate wiring only. Capacity conclusions require the documented 30-minute run and soak profile.
- Optimize measured hot paths first. Preserve a readable, tested fallback when an optimization changes storage or wire format.

## Build and verification

```powershell
dotnet restore ChatApp.Server.sln
dotnet build ChatApp.Server.sln -c Release --no-restore
dotnet test tests/ChatApp.Server.IntegrationTests/ChatApp.Server.IntegrationTests.csproj -c Release --no-build
```

Run focused tests while iterating, then the full Release build and test project. Docker or external-service tests may be reported as skipped only with the missing dependency stated.

## Working tree safety

This repository is frequently edited from multiple tools. Inspect `git status` and the relevant diff before modifying a file. Preserve unrelated user changes and never use destructive reset/checkout commands to clean the tree.
