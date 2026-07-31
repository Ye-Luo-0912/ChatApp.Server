# 性能基线目录

本目录存放 k6 压测的基线 JSON 输出，供 PR 回归门禁比对。

## 文件命名

```
baseline-<PROFILE>-rate<RATE>.json
```

例如 `baseline-steady-rate20.json` 表示 steady 场景、20 req/s 的基线。
基线由 Nightly 跑 15 分钟生成（样本更稳定），PR 用 3 分钟跑比对；
p95/p99 是分位数，时长不同只影响样本量，不影响分位数值，因此可比。

## 生成方式

基线由 Nightly workflow 自动更新（见 `.github/workflows/performance-nightly.yml`），
跑完后将当前 master 分支的 k6 JSON 输出提交到本目录。

首次生成或手动刷新：

```bash
# 起依赖 + Performance Server
k6 run -e PROFILE=steady -e RATE=20 -e DURATION=15m \
  --out json=tests/load/baselines/baseline-steady-rate20.json \
  tests/load/mixed-workload.k6.js
git add tests/load/baselines/baseline-steady-rate20.json
git commit -m "perf: refresh steady baseline"
```

## 比对

PR 回归门禁调用 `tests/load/compare-baseline.mjs`，阈值见脚本头部注释：

| 维度 | 阈值 |
|------|------|
| p95  | ≤ +8% |
| p99  | ≤ +12% |
| 错误率 | < 0.1%（绝对目标） |

宿主侧指标（allocations/Redis commands/DB queries）从 `/debug/metrics` 端点采样，
经 k6 `setup()/teardown()` 计算差值后由 `compare-baseline.mjs` 转为 per-request 阻塞门禁：
allocations/request ≤ +10%、Redis commands/request 不得增加、DB queries/request 不得增加。

## 注意

- 基线仅在固定 runner（`ubuntu-24.04`）上有效；自托管 runner 需重新校准。
- 硬件变更后必须重新生成基线，并在 commit message 中注明硬件配置。
- 不要手工编辑基线 JSON——它是 k6 的原始事件流。
