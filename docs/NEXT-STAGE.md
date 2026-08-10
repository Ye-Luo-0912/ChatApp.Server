# 下一阶段与接手状态

## 职责

Server 是账户、隐私、好友关系、HTTP 附件和安全策略的业务权威；PostgreSQL + Outbox 是持久化边界，Realtime 只消费明确的投影/命令。

## 下一步执行与交接

当前接手批次是 `REL-GATE-1`。Server 不再新增关系协议，而是为 Realtime 隔离门禁提供冻结的权威数据面。

1. **交付输入。** 固定 Contracts `2.5.2`、Integration `3.1.3`、Migration/数据库快照、Server commit 与 Release 二进制 hash；向隔离 Rebuilder 提供只读服务密钥和 stream/snapshot/digest 地址，密钥不写入配置、命令、日志或报告。
2. **配合故障矩阵。** 覆盖分页极限、空但有水位的 stream、接近 `100,000` 项、并发 mutation、旧/重复快照、迟到 delta、429/5xx/超时和密钥轮换；HTTP 关系读写始终在线且保持唯一权威。
3. **交付证据。** 与 Realtime 一起归档 `run-manifest.json`、reconcile report 和故障矩阵；manifest 记录版本/hash/非敏感配置，工具报告只记录分页与差异。故障恢复后再次全量通过，才允许 Shared 接手 `REL-WIRE-2`。
4. **边界不变。** 后续只读 canary 失败时只关闭 Rebuilder/Reads/capability；不回滚 Server mutation、版本时钟或已提交 Outbox，不把 SDP、媒体或 TCP codec 引入 Server。

交给下一仓时必须明确列出稳定字段/错误语义和仍未覆盖的外部故障项，不能只写“测试通过”。

## 接手状态

- P0（在线入口已收口）：HTTP Server 是唯一关系读写权威，Realtime 默认关系读写/同步入口已 fail-closed。
- P0（版本化增量已完成）：已升级本地不可变包 `ChatApp.Realtime.Contracts 2.5.2` / `ChatApp.Realtime.Integration 3.1.3`。好友申请、接受/拒绝/撤回、拉黑/解除和删除好友在原业务事务内批量分配 `(ownerUserId, listType)` 连续版本，并把在线失效信息与 `RelationshipProjectionDelta v1` 合并为同一 Realtime Outbox 事件；event id 由 owner/list/version 稳定派生，事务回滚时关系写、version clock 和 Outbox 一起回滚。真实 PostgreSQL 并发测试已覆盖同一 owner、不同关系对同时写入仍得到连续 `1,2`。
  - delta 固定 schema version、event id、version、actor、owner/list type、resource id、upsert/delete、subject、状态、消息和 occurred-at；旧 Gateway 忽略新增 `Projection` 字段，仍可把事件当在线失效通知，未形成第二条事件或双写窗口。
- P0（流级权威快照、自动扫描与只读候选已完成，生产切读仍是 TODO）：已提供服务密钥保护的 stream 分页、单 stream 快照和单 stream digest 端点。快照与 digest 都在 PostgreSQL `REPEATABLE READ` 下同时读取列表与连续 version；digest 只返回 owner/list/version/count/hash/captured-at，不构造或传输好友明细，供轻量对账使用。完整快照仍包含确定 snapshot id，单 stream 上限 `100,000` 项，空但有水位的列表不会被遗漏。Realtime 自动扫描器使用数据库时钟和 owner+claim-token fencing，持久化复合 cursor 只在整页成功后推进，失败续跑并重复全量扫描；重复同版本快照会核对 count/hash 并只修复差异 stream。Realtime 的 snapshot-gated list processor 已实现但默认关闭，无 checkpoint 的 stream 明确 unavailable，分页 version 变化要求重启。候选包 SHA-256：Contracts `30F4429C3BD2E1F384456AB948CD08898448D4D5BB99696D9733EE42D8900583`，Integration `F319AAD311E735AA766C6C04F77454B366CAC4B49A826A356E8B9F75889C55C4`。
  - 编排上线 TODO：Realtime 受保护的 status/streams/reconcile 端点已提供持久化 cursor、稳定轮次、租约/最后错误，以及逐 stream current/snapshot version、数量/hash 和本地 delta 连续性，不返回列表内容。下一步用独立只读服务密钥在隔离环境启用：启动两个 Rebuilder 实例验证只有一个租约 owner；分别在取页、导入和提交 cursor 前中断进程，确认过期接管从持久化 cursor 续跑；对 429/5xx/超时执行有界退避；轮换密钥时旧密钥立即失效且日志/报告不含密钥；扫描中插入更小 owner id 后下一整轮必须覆盖。密钥只来自环境/secret store，不写 appsettings、日志或报告。
  - 对账执行 TODO：使用 Realtime 的 `scripts/Invoke-RelationshipProjectionReconcile.ps1`，密钥只由 secret store 注入环境变量。工具自动遍历 `nextOwnerUserId/nextListType`、校验 Rebuilder 状态在每轮前后未变化，并要求连续两轮全量指纹相同；报告记录分页摘要和差异项，源码/包 hash 与非敏感配置写入同目录 manifest，二者都不含密钥或好友明细。HTTP 409 必须按 `server/realtime stream missing`、version、count、checkpoint hash 或 local gap 分类，503 是能力/上游不可用而不是零差异；任一结果均阻断。先让自动 Rebuilder 修复同版本 count/hash 漂移，禁止用旧快照覆盖更高水位。空列表、接近 `100,000` 项、并发 mutation、重复/旧快照、迟到 delta、取消续跑和密钥轮换都通过，且故障恢复后工具再次通过，才接告警和只读 canary。
  - 上线顺序固定为 migration → delta 影子消费 → 快照回填 → 连续版本/数量/hash 对账 → TCP 只读 canary；任一门禁失败只关闭投影读取和回填编排，HTTP mutation 与现有 Outbox 不回滚。
- P1：为好友/隐私/附件混合负载建立成对短测，按 SQL、WAL、分配和 p95/p99 优化。
  - 固定同一数据快照与负载种子，分别采集基线/候选的 SQL 次数、WAL/请求、索引命中、CPU、managed allocation 和尾延迟；一次只改变一个因素。
  - 短测用于回归和定位，候选冻结后再跑 30 分钟；只有发布候选才进入 soak，报告必须保存命令、配置与源码/二进制 hash。
- P1：steady/endpoint 压测的令牌生命周期代码已按真实客户端行为修正（启动刷新、401 刷新重放、每轮/每场景重新生成预设令牌）；stage gate 尚待短测，并须确认 `tests/load` 产物不入库。
  - 先覆盖正常刷新、并发 401 单飞刷新、重放只执行一次、过期 refresh token 和撤销设备；通过后再纳入混合场景，失败时区分认证错误与业务容量错误。
  - 完成标准：测试产物写隔离目录并由 ignore 守卫，预设令牌不入报告/日志，负载结束后无遗留会话或临时凭据。
- P1：补 Push token、通知偏好、附件扫描/保留策略与管理审计的端到端闭环。
  - 明确 token 设备归属与轮换、通知免打扰/隐私裁剪、附件扫描失败与隔离、过期回收和管理员操作审计；所有外部回调都要求幂等键和可重放状态机。
- P2（通话服务面）：只提供通话策略、风控、审计和短期 TURN 凭据；媒体不进入 Server 数据库、Outbox 或 TCP Gateway。以直连/STUN/TURN 比例、建连成功率和 TURN 带宽成本决定是否进入下一阶段，不新增裸 UDP 业务入口。
- 当前验证：隔离 Release 构建 0 warning/error；Integration `249/249` 通过，3 个需外部故障环境的用例按条件跳过；关系快照/digest/服务密钥聚焦 `7/7`；k6 脚本语法通过。

## 功能路线

语音消息沿用附件 API，并增加 MIME/codec/duration 校验与扫描策略；实时通话只由 Server 提供会话策略、风控和短期 TURN 凭据，媒体面放到 WebRTC/TURN/SFU 组件。

## 验证顺序

聚焦领域/契约测试 → Release 构建 → 短时混合负载；阶段长测与发布 soak 只在数据规模、索引和功能冻结后执行。
