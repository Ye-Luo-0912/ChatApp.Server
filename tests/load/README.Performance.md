# Performance 压测环境

## 启动

```bash
ASPNETCORE_ENVIRONMENT=Performance dotnet run --project ChatApp.Server.csproj
```

`appsettings.Performance.json` 将登录/刷新等限流放宽到约 10000/min，并提高 BCrypt / 通知 Outbox / 头像重编码并发，**仅用于容量测试**。

## 与限流测试分离

| 场景 | 环境 | 说明 |
|------|------|------|
| 限流专项 | Production/Development 默认配额 | 验证 429 与策略 |
| 双实例限流 | 两实例 + 共享 Redis，默认限流 | `mixed-workload.k6.js` `PROFILE=dual_ratelimit` |
| 登录容量 | Performance | `login-capacity.k6.js` |
| 混合负载 | Performance | `mixed-workload.k6.js` `PROFILE=mixed` |
| 在线用户 | Performance + 预置 Token | `online-users.k6.js`，`-e TOKENS_FILE=./tokens.json` |

## 混合负载（推荐基线）

固定硬件上跑 10 分钟混合到达率，覆盖登录、刷新、好友、搜索、通知：

```bash
k6 run -e BASE_URL=http://localhost:8080 -e CREDS_FILE=./creds.json -e PROFILE=mixed \
  -e RATE=20 -e DURATION=10m tests/load/mixed-workload.k6.js
```

记录：

| 指标 | 来源 |
|------|------|
| p95 / p99 / 错误率 | k6 Trends + `errors` Rate |
| 登录过载 503 | k6 `login_overloaded_503`（BCrypt 闸门） |
| Redis 延迟 | OTEL / `Infrastructure.Runtime` `redis.ping.duration` |
| 通知积压 | `notification.outbox.backlog` |
| 头像队列 | `avatar.reencode.wait` / `queue_depth` / `rejected` |
| DB 连接池等待 / GC | 进程指标 + `Infrastructure.Runtime` GC gauges |

## 预置 Token 格式

```json
[
  { "accessToken": "...", "userId": 1, "refreshToken": "..." }
]
```

避免 setup 阶段被登录限流截断后多个 VU 共享少量会话。

## 后续

- 十万用户、百万好友关系种子数据
- FriendshipService 显式事务优化前先用查询与事务指标确认收益
