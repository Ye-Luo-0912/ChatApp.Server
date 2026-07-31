# Performance 压测环境

## 启动（Server）

依赖（Postgres + Garnet；可选 NATS 给 Realtime / AccountCleanup E2E）：

```powershell
$env:POSTGRES_PASSWORD = "your-local-password"
docker compose -f docker-compose.yaml up -d postgres_db garnet_cache
# 需要 Realtime Outbox / AccountCleanup 回传时再起单节点 NATS（本地默认）：
# docker compose -f ../ChatApp.RealtimeServices/docker-compose.nats.yaml up -d nats
# 可选三节点集群：另加 -f docker-compose.nats.cluster.yaml --profile nats-cluster
```

应用迁移后起 API（Performance 宽限流，**仅容量测试**）：

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Performance"
$env:ASPNETCORE_URLS = "http://localhost:8080"
dotnet ef database update --project Infrastructure --startup-project Infrastructure
dotnet run --project ChatApp.Server.csproj --no-launch-profile
```

`appsettings.Performance.json` 将登录/刷新等限流放宽到约 10000/min，并提高 BCrypt / 通知 Outbox / 头像重编码并发。

导出 OTEL（可选）：

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
```

## 启动（Realtime，可选基线）

AccountCleanup / Outbox 端到端或实时管道基线时另开 Realtime（详见 `../ChatApp.RealtimeServices/README.realtime-containers.md`）：

```powershell
# 1) 单节点 NATS（容器名 chatapp_nats，端口 4222/8222）
docker compose -f ..\ChatApp.RealtimeServices\docker-compose.nats.yaml up -d nats

# 2) 本机 Realtime（需 SDK 11 preview；仓库 global.json 已钉该版本）
$env:DOTNET_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://127.0.0.1:8080"   # 若 Server 占用 8080 则改端口
dotnet run --project ..\ChatApp.RealtimeServices\ChatApp.RealtimeServices\ChatApp.RealtimeServices.csproj -c Release --no-launch-profile
```

连接串放在 `~/.chatapp/realtime.user.json`。健康检查：`GET http://127.0.0.1:8080/ready`（NATS/Postgres/Garnet 均 healthy 才 200）。

### Realtime 管道短冒烟（≤1m）

在 TCP 网关仓库执行（Realtime 已 `/ready`，NATS 已起）：

```powershell
cd ..\ChatAppTCP_Server
dotnet run --project .\tools\ChatApp.Realtime.PipelineLoadGenerator -c Release -- `
  --nats-url nats://127.0.0.1:4222 `
  --warmup-seconds 5 --duration-seconds 30 `
  --concurrency 4 --operations-per-second 4 --payload-bytes 512 `
  --report-directory ..\ChatApp.Server\tests\load\realtime-pipeline-smoke-<date>
```

正式 30m / soak / 编排器基线见 `../ChatAppTCP_Server/docs/performance-baseline.md`（勿把短冒烟当容量承诺）。

停止依赖（需要时）：

```powershell
# 停 Realtime 进程后：
docker compose -f ..\ChatApp.RealtimeServices\docker-compose.nats.yaml stop nats
# 或连同 Postgres/Garnet：docker stop chatapp_nats chatapp_postgres chatapp_garnet
```

## 与限流测试分离

| 场景 | 环境 | 说明 |
|------|------|------|
| 限流专项 | Production/Development 默认配额 | 验证 429 与策略 |
| 双实例限流 | 两实例 + 共享 Redis，默认限流 | `mixed-workload.k6.js` `PROFILE=dual_ratelimit` |
| 登录容量 | Performance | `login-capacity.k6.js` 或 `PROFILE=auth_capacity` |
| 稳态业务 | Performance | `PROFILE=steady`（固定设备，≥90% 已认证） |
| 设备抖动 | Performance | `PROFILE=device_churn`（每轮新 `X-Installation-Id`） |
| 浸泡 | Performance | `PROFILE=soak`（默认 **2h**，同 steady 流量形态） |
| 在线用户 | Performance + 预置 Token | `online-users.k6.js`，`-e TOKENS_FILE=./tokens.json` |

> 旧名 `PROFILE=mixed` 仍可用，行为等同 `device_churn`（历史：每轮登录并轮换设备）。基线请改用 `steady`。

## 推荐场景

### 两层性能门禁

| 门禁 | workflow | 触发 | 场景 | 阻塞 PR |
|------|----------|------|------|---------|
| PR 回归 | `.github/workflows/performance-regression.yml` | `pull_request` to master | steady 3m + 比对基线 | 是（回归超阈值或绝对目标未达） |
| Nightly | `.github/workflows/performance-nightly.yml` | `schedule` 每日 / 手动 | steady 15m、auth capacity、device churn、双实例限流、重启恢复、2h soak | 否（仅归档 + 刷新基线） |

PR 回归阈值（相对基线，[compare-baseline.mjs](compare-baseline.mjs) 实现）：

| 维度 | 阈值 |
|------|------|
| p95 | ≤ +8% |
| p99 | ≤ +12% |
| 错误率 | < 0.1%（绝对目标） |
| allocations/request | ≤ +10%（待 `/debug/metrics` 上线，当前 info-only） |
| Redis commands/request | 不得增加（待上线，info-only） |
| DB queries/request | 不得增加（待上线，info-only） |

初始绝对目标（[absolute-goals.json](absolute-goals.json)，按固定硬件校准后调整）：

| 场景 | 初始目标 |
|------|----------|
| warmed authenticated read | p95 ≤ 50 ms，p99 ≤ 150 ms |
| refresh | p95 ≤ 100 ms，p99 ≤ 250 ms |
| steady 错误率 | < 0.1% |
| 简单读取分配 | ≤ 8 KB/request（待上线） |
| L1 上线后 Redis | ≤ 0.2 command/request（待上线） |
| soak | 预热后内存无持续单调增长 |
| Worker | backlog 和 oldest age 不持续增长 |

基线文件存放于 [baselines/](baselines/) 目录，命名 `baseline-<PROFILE>-rate<RATE>.json`。
Nightly 每日跑完 steady 15m 后自动 commit/push 刷新基线；PR 拉取该基线与当前分支 3m run 比对。
首次运行（基线不存在）仅校验绝对目标与错误率，不阻塞。

Nightly 重启恢复：在 steady 压测中段 `docker restart` Postgres + Garnet，轮询 `/health/ready` 恢复后继续压测 30s，
断言重启后错误率 ≤ 5%。覆盖 AGENTS.md 的恢复语义。

### steady（固定设备基线）

固定 `X-Installation-Id`（`k6-steady-installation-{vu-padded}`，符合服务端长度校验），默认 `LOGIN_RATIO=0.1`（≤10% 登录，≥90% me/friends/search/notifications/sessions/refresh）。登录响应中的 `DeviceCredential` 只用于后续 refresh，禁止把它写入基线或日志。优先配合预置 Token，避免登录限流污染业务基线：

```bash
k6 run -e BASE_URL=http://localhost:8080 -e TOKENS_FILE=./tokens.json -e PROFILE=steady \
  -e RATE=20 -e DURATION=10m tests/load/mixed-workload.k6.js
```

无 Token 时可退回凭据文件（会按比例登录）：

```bash
k6 run -e CREDS_FILE=./creds.json -e PROFILE=steady -e RATE=20 -e DURATION=10m \
  tests/load/mixed-workload.k6.js
```

### auth_capacity（登录 / BCrypt 容量）

```bash
k6 run -e CREDS_FILE=./creds.json -e PROFILE=auth_capacity -e RATE=30 -e DURATION=5m \
  tests/load/mixed-workload.k6.js
# 或专用脚本:
k6 run -e CREDS_FILE=./creds.json tests/load/login-capacity.k6.js
```

### device_churn（新设备 / 会话 / 通知增长）

每轮 `X-Installation-Id=k6-churn-installation-{vu-padded}-{iter-padded}` 并登录，观察会话索引、新设备通知、可信设备相关压力：

```bash
k6 run -e CREDS_FILE=./creds.json -e PROFILE=device_churn -e RATE=10 -e DURATION=10m \
  tests/load/mixed-workload.k6.js
```

### soak（≥2h 浸泡）

与 steady 相同流量形态（固定设备 + 低登录比），默认 `DURATION=2h`、较低到达率。关注内存、GC、连接池、`notification.outbox.backlog`、`data_export.pending`、文件句柄：

```bash
k6 run -e TOKENS_FILE=./tokens.json -e PROFILE=soak -e RATE=8 -e DURATION=2h \
  tests/load/mixed-workload.k6.js
```

覆盖默认时长：

```bash
k6 run -e PROFILE=soak -e DURATION=2h -e TOKENS_FILE=./tokens.json tests/load/mixed-workload.k6.js
```

### 短冒烟（Testcontainers / 本地 CI）

Docker 可用时跑集成测试（含 Postgres/Redis；不可用则 skip）。迁移须与模型一致，**不要**再 Suppress `PendingModelChangesWarning`：

```bash
dotnet test tests/ChatApp.Server.IntegrationTests/ChatApp.Server.IntegrationTests.csproj -c Release \
  --filter "FullyQualifiedName~AccountDeletionCleanup|FullyQualifiedName~DataExportPersistence|FullyQualifiedName~AccountCleanupSaga|FullyQualifiedName~AccountSecurity"
```

可选短压测冒烟（需已启动 Performance Server + k6；非浸泡，约 1–2 分钟）：

```bash
k6 run -e BASE_URL=http://localhost:8080 -e PROFILE=steady -e RATE=5 -e DURATION=1m \
  tests/load/mixed-workload.k6.js
```

无预置 Token 时用凭据文件（会触发登录，易碰到限流以外的冷启动噪声）：

```bash
k6 run -e BASE_URL=http://localhost:8080 -e CREDS_FILE=./creds.json -e PROFILE=steady \
  -e RATE=5 -e DURATION=1m tests/load/mixed-workload.k6.js
```

### 基线记录表（每次跑完填一行；未跑勿填造数据）

`extract-summary.mjs` 同时输出 `iterations_per_second` 和 `http_requests_per_second`。前者是业务迭代速率，后者是 HTTP 请求速率；steady 的一次迭代包含多个请求，不能混用。

| 日期 | 硬件 | PROFILE | RATE | 时长 | login p95/p99 | refresh p95 | errors | 503 overload | 备注 |
|------|------|--------|------|------|---------------|-------------|--------|--------------|------|
| 2026-07-21 | local (Win11 + Docker Desktop) | steady | 5 | 1m | 82ms / 92ms | 20ms | 0% | 0 | **HTTP smoke 实测**：301 iters / 2107 HTTP reqs，~34.9 req/s，checks 100%；login p95/p99=82/92ms，refresh p95=20ms；Garnet 需 --lua；k6 脚本已修 /api/auth/refresh-token + snowflake userId 精度；原始输出 `tests/load/k6-smoke-2026-07-21.txt` |
| 2026-07-21 | local (Win11 + Docker Desktop) | realtime-pipeline | 4 ops/s · c=4 | 5s+30s | n/a（管道） | n/a | 0% | n/a | **Realtime smoke 实测**：单节点 `chatapp_nats` + Realtime `/ready`；124/124 完整链路，4.09 pipeline/s；complete p50/p95/p99=50/68/82.5ms；NATS ping 1.17ms；原始输出 `tests/load/realtime-pipeline-smoke-2026-07-21.txt` + `tests/load/realtime-pipeline-smoke-2026-07-21/`；非正式 30m 容量基线 |

### 已知缺口（不阻塞基线）

| 项 | 状态 |
|----|------|
| S3 导出对象加密（SSE/KMS） | Local AES-GCM 已落地；切 S3 时仍需 SSE-S3/KMS |
| DB 重启后 Outbox/Saga E2E | 未自动化；依赖 RealtimeIntegration:Url + NATS |
| AccountCleanup DLQ 专用流 | 无；超时 Failed（`pending_timeout`）作对账兜底 |

### 宿主侧同步采集

| 指标 | Meter / 名称 |
|------|----------------|
| 登录结果分布 | `Infrastructure.Auth` → `auth.login`（outcome） |
| BCrypt 耗时 / 等待 / 过载 | `password.hashing.duration` / `wait` / `overloaded` / `in_flight` |
| 可信设备 | `trusted_device.ops` |
| 导出积压 | `data_export.pending` / `data_export.duration` |
| 登录风险 | `login_risk.signals` |
| Redis 延迟 | `redis.ping.duration` |
| 通知积压 | `notification.outbox.backlog` |
| 头像队列 | `avatar.reencode.*` |
| 证据查询 | `moderation.evidence.*` |
| GC / 工作集 | `Infrastructure.Runtime` |

## 预置 Token 格式

```json
[
  {
    "accessToken": "...",
    "refreshToken": "...",
    "deviceCredential": "...",
    "userId": "9223372036854775807",
    "deviceId": "perf-installation-00000001"
  }
]
```

`userId` 必须是十进制字符串，避免 Snowflake ID 经 JavaScript `Number` 丢失精度；`deviceId` 必须复用登录时的 InstallationId，refresh 才能通过设备绑定校验。避免 setup 阶段被登录限流截断后多个 VU 共享少量会话。

## 解读建议

1. **login p95 飙高 + `password.hashing.overloaded` / `wait` 上升** → 调高 Performance 的 `PasswordHashing:MaxConcurrentOperations`，或降 RATE。
2. **`notification.outbox.backlog` 持续增长** → 提高 Outbox worker 并发，或检查下游推送；`device_churn` 下更易放大。
3. **refresh 远慢于 login 的 AT 校验** → 查 Redis ping 与 Garnet CPU。
4. **双实例 429 不明显** → 确认共享同一 Redis 且未开 Performance 宽限流。
5. **soak 内存/文件/连接单调爬升** → 查导出临时文件、头像临时对象、会话泄漏与池配置。

## 后续

- 十万用户、百万好友关系种子数据
- FriendshipService 显式事务优化前先用查询与事务指标确认收益
