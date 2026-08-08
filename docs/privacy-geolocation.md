# GeoIP 隐私与传输约束

登录风险分析优先读取本地 CIDR GeoIP 数据库，不会在本地命中时向外部发送 IP。只有显式设置 `GeoLocation:AllowExternalFallback=true` 时，才会把经过公网地址校验的连接 IP 发送到 `GeoLocation:BaseUrl` 配置的 HTTPS GeoIP provider。

本地文件格式为每行 `network/prefix|country|city`，例如 `203.0.113.0/24|Example|Example City`；通过 `GeoLocation:LocalDatabasePath` 配置绝对路径或相对于应用目录的路径。生产环境应使用经过批准的内部 GeoIP 数据发布流程生成该文件。

- Redis 缓存键使用 `Security:SecretEncryptionKey` 派生的 HMAC，不保存原始 IP。
- 应用日志和安全通知只显示 IPv4 `/24` 或 `IPv6/64` 脱敏值；禁止记录完整 provider 响应。
- `SecurityEvent`/session 中的原始 IP 属于安全审计数据，必须按产品隐私政策配置数据库访问控制、保留期限和删除流程。
- 更换 provider 时必须复核其服务条款、跨境传输、数据保留和 DPA；若无法满足政策，应切换本地 GeoIP 数据库。
