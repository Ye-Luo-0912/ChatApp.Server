# 性能基线目录

本目录存放 k6 压测的紧凑基线 JSON，供阶段性性能门禁比对。

## 文件命名

```
baseline-<PROFILE>-rate<RATE>.json
```

例如 `baseline-steady-rate20.json` 表示 steady 场景、20 req/s 的基线。
候选基线由手动长测跑 15 分钟生成（样本更稳定），快速阶段门禁用 3 分钟跑比对；
p95/p99 是分位数，时长不同只影响样本量，不影响分位数值，因此可比。

## 生成方式

手动运行 `.github/workflows/performance-nightly.yml` 生成候选基线。
工作流只上传候选 summary 和原始事件流 artifact，不直接写入 master。
审阅 runner、样本量和结果后，通过普通提交更新本目录。
如需本地生成：

```bash
# 起依赖 + Performance Server
k6 run -e PROFILE=steady -e RATE=20 -e DURATION=15m \
  --out json=steady-15m.json \
  tests/load/mixed-workload.k6.js
node tests/load/extract-summary.mjs \
  --input steady-15m.json \
  --output tests/load/baselines/baseline-steady-rate20.json \
  --runner "$PERF_RUNNER_ID" \
  --runtime "steady 15m RATE=20"
git add tests/load/baselines/baseline-steady-rate20.json
git commit -m "perf: refresh steady baseline"
```

## 比对

阶段门禁调用 `tests/load/compare-baseline.mjs`，阈值见脚本头部注释：

| 维度 | 阈值 |
|------|------|
| p95  | ≤ +8% |
| p99  | ≤ +12% |
| 错误率 | < 0.1%（绝对目标） |

宿主侧指标（allocations/Redis commands/DB queries）从 `/debug/metrics` 端点采样，
经 k6 `setup()/teardown()` 计算差值后由 `compare-baseline.mjs` 转为 per-request 阻塞门禁：
allocations/request ≤ +10%、Redis commands/request 不得增加、DB queries/request 不得增加。

## 注意

- `ubuntu-24.04` 只固定 runner 镜像，不是物理硬件身份；正式基线必须使用实际
  `PERF_RUNNER_ID`。runner 不匹配时 Stage Gate 会自动执行同机 paired baseline。
- 硬件变更后必须重新生成基线，并在 commit message 中注明硬件配置。
- 不要手工编辑基线 JSON——它应由 `extract-summary.mjs` 从原始事件流生成。
