# 下一阶段与接手状态

## 职责与优先级

Server 是账户、隐私、好友关系、HTTP 附件和安全策略的业务权威；PostgreSQL + Outbox 是持久化边界，Realtime 只消费明确的投影/命令。

当前优先完成可被 Client 使用的产品功能：关系读取对照支持 → 语音消息安全闭环 → 通话 grant 与凭据。数据库优化只处理这些功能暴露出的真实热点，不单独抢占主线。

## 已完成前置

关系 HTTP mutation 已收口为唯一权威；好友、申请和黑名单变更在原业务事务内推进 owner/list 连续版本并写入同一 Outbox 事件。权威 stream、snapshot 与 privacy-minimized digest 已可供 Realtime 重建和对账。下一阶段不再扩展第二套关系写协议。

## 当前 P0：`REL-E2E-4` 权威对照支持

1. 固定 owner/list/version、资源键、状态、可空消息和删除语义；stream、snapshot、digest 对同一数据库快照必须给出一致 version/count，空列表也保留已提交水位。
2. 为端到端测试提供最小的 HTTP 权威 list/digest 对照，覆盖好友申请接受/拒绝/撤回、删除好友、拉黑/解除、并发变化、重复请求、空列表和授权差异。
3. 不存在、无权、输入非法、快照变化和上游不可用不能折叠成空结果；测试/运维接口不得泄漏好友明细、token 或隐私字段。
4. 若 Gateway/Client 对照发现差异，只修投影事件、快照或读取语义；HTTP mutation、版本时钟和已提交 Outbox 继续保持唯一权威。

完成标准：同一快照下 Server 与 Realtime/Gateway/Client 的 list/version/count/资源键一致；并发 mutation 后通过 delta 或 snapshot 收敛，没有第二权威或双写窗口。

## 下一阶段功能

### P1：`VOICE-MSG-2` 语音消息元数据与安全

1. 语音继续复用附件上传、扫描、绑定、下载授权和保留流程；补齐 codec/container、MIME、duration、sample rate、channels、size 与可选 waveform 的有界模型和跨字段校验。
2. 服务端不能只信客户端声明。校验 MIME/容器组合、大小、时长和音频头；为截断文件、未知 codec、恶意内容、解析失败和扫描超时定义稳定状态。
3. 只有 `Available` 附件可绑定消息。重复确认和重试必须幂等；Server 不转码，不把音频正文写入 PostgreSQL 或 Outbox，只保留对象引用、状态和必要审计。
4. 明确隔离、拒绝、过期和孤立对象回收；下载票短期有效、绑定账户/附件/用途，日志不记录对象密钥或票据。

完成标准：正常语音、伪造 MIME、超时长、截断、扫描拒绝、重复确认和过期回收都有领域/集成测试，并可与 Realtime 语音附件状态逐项对照。

> 状态（2026-09-02）：**已交付并真机验收**。上传白名单补 `audio/wav`（客户端录音即 WAV，此前真实语音消息
> 在 presign 阶段即被拒，relgate 已部署修复）；扫描/绑定/下载授权门禁不变。集成测试已恢复无 Docker 运行
> （`CHATAPP_TEST_POSTGRES` / `CHATAPP_TEST_GARNET` 直连本机原生基础设施），268 过 / 3 跳过。

### P1：`CALL-E2E-2` 通话资格与短期凭据

1. 实现 call grant：绑定发起者、被叫、设备/会话、用途、签发时间、过期时间、nonce 和版本；好友、拉黑、隐私、频率和设备风控在签发时 fail-closed。
2. 定义单次/重放、撤销和权限变化语义；Realtime 必须能离线校验签名与 audience，不能反向写 Server 状态来延长 grant。
3. 按需签发短期 TURN 凭据，设置最小 TTL、并发与流量限制，记录低基数的签发/拒绝/过期指标。
4. Server 不接收或持久化 SDP、ICE candidate 或媒体包，不新增裸 UDP 业务入口；WebRTC/TURN/SFU 属于独立媒体面。

完成标准：合法、过期、篡改、重放、拉黑变化、多设备和频率超限均有测试；关闭通话能力不影响聊天和附件。

### P1：`ACCOUNT-OPS-1` 账户与设备完整性

补齐 Push token 设备归属、轮换和撤销，通知偏好/免打扰、设备会话与安全设置，以及附件隔离/回收的最小审计。仅在关系与语音主链路不被阻塞时并行推进。

## 支撑项：`DB-HOTPATH-1`

1. 只针对当前功能链路的 Top endpoint/SQL 做固定快照短 A/B，记录往返、WAL/request、pool wait、allocation 与 p95/p99。
2. 优先消除重复存在性查询、宽实体物化、无变化 UPDATE 和逐行 Outbox；使用窄投影、`RETURNING`、`ON CONFLICT` 或有界批处理时必须保留事务、版本与幂等语义。
3. 不跨请求/事务复用 `DbContext`、command、reader 或可变聚合；不要为了理论收益引入难维护的巨大 SQL。

没有 profiler/SQL 证据或稳定收益时，不改现有实现。

## 跨仓衔接

Server 提供关系权威、附件安全策略和短期 call grant；Realtime 构建投影与临时信令；Shared 固定外部 wire；Gateway 只做鉴权上下文、映射和路由；Client 负责本地事务、设备与媒体体验。TURN/SFU 独立规划，不经 Server 数据库、Outbox 或 TCP Gateway 转发音频。

## 验证顺序

聚焦领域/契约测试 → Release 构建 → 必要的 5–10 分钟同剖面短测。当前阶段到功能联调验收为止。
