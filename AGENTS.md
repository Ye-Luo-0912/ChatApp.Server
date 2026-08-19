# AGENTS.md — ChatApp Server

Guidance for humans and coding agents working in this repository.

接手时先读相关实现、调用方、迁移和测试，确认现有语义与数据不变量后再改。优先级依次是正确/安全、功能链路完整、可维护、可测量的性能、真实复用；只共享所有权清楚、线程安全且生命周期匹配的稳定资源，禁止共享 `DbContext`、可变会话、事务或流。验证按聚焦单测/契约测试 → Release 构建 → 短时 smoke 推进。当前路线见 `docs/NEXT-STAGE.md`。

## Architecture boundaries

| Layer | Path | May depend on | Must not |
|---|---|---|---|
| Core | `Core/` | BCL only | ASP.NET Core, EF Core, Redis, NLog, Infrastructure |
| Infrastructure | `Infrastructure/` | Core, EF Core, Redis/Garnet, Realtime integration | Controllers or HTTP response types |
| Host/API | `Controllers/`, `Middlewares/`, `Filters/`, `Program.cs` | Core and Infrastructure | Persistence details in controllers |
| Tests | `tests/` | Public behavior and explicitly exposed internals | Depend on execution order or shared mutable fixtures |

Dependency direction is **Host → Infrastructure → Core**. Realtime contracts, NATS integration, and EF Outbox mapping are consumed from the independently versioned `ChatApp.Realtime.Contracts`, `ChatApp.Realtime.Integration`, and `ChatApp.Realtime.Outbox.EntityFrameworkCore` packages in the repository-local feed; Server restore/build must not require a sibling source checkout. Consumers that do not persist the EF Outbox must not reference the Outbox package.

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
- Same-profile short tests drive regression checks and tuning. Longer stability runs are outside the
  current feature-development route.
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
