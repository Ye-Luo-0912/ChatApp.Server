# GeoIP 隐私与传输约束

登录风险分析只把经过公网地址校验的连接 IP 发送到 `GeoLocation:BaseUrl` 配置的 GeoIP provider，默认使用 HTTPS。生产环境可替换为本地 GeoIP 数据库，避免向第三方传输地址。

- Redis 缓存键使用 `Security:SecretEncryptionKey` 派生的 HMAC，不保存原始 IP。
- 应用日志和安全通知只显示 IPv4 `/24` 或 `IPv6/64` 脱敏值；禁止记录完整 provider 响应。
- `SecurityEvent`/session 中的原始 IP 属于安全审计数据，必须按产品隐私政策配置数据库访问控制、保留期限和删除流程。
- 更换 provider 时必须复核其服务条款、跨境传输、数据保留和 DPA；若无法满足政策，应切换本地 GeoIP 数据库。
