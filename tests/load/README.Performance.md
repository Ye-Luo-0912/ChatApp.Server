# Performance 压测环境

## 启动

```bash
ASPNETCORE_ENVIRONMENT=Performance dotnet run --project ChatApp.Server.csproj
```

`appsettings.Performance.json` 将登录/刷新等限流放宽到约 10000/min，**仅用于容量测试**。

## 与限流测试分离

| 场景 | 环境 | 说明 |
|------|------|------|
| 限流专项 | Production/Development 默认配额 | 验证 429 与策略 |
| 登录容量 | Performance | `login-capacity.k6.js` |
| 在线用户 | Performance + 预置 Token | `online-users.k6.js`，`-e TOKENS_FILE=./tokens.json` |

## 预置 Token 格式

```json
[
  { "accessToken": "...", "userId": 1, "refreshToken": "..." }
]
```

避免 setup 阶段被登录限流截断后多个 VU 共享少量会话。

## 后续

- 搜索 / 刷新等 Trend 使用独立 PROFILE 与独立频率
- 十万用户、百万好友关系种子数据
- 记录 CPU、GC、连接池、Redis、Outbox、安全事件写入耗时
- FriendshipService 显式事务优化前先用查询与事务指标确认收益
