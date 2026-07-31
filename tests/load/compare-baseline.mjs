#!/usr/bin/env node
// k6 JSON 输出与基线 JSON 比对，用于 PR 回归门禁。
//
// 用法：
//   node compare-baseline.mjs --baseline <path> --current <path> [--absolute <path>] [--strict]
//
// 退出码：
//   0 = 全部通过
//   1 = 至少一项回归或绝对目标未达成
//   2 = 输入错误
//
// 回归阈值（相对基准分支）：
//   p95        ≤ +8%
//   p99        ≤ +12%
//   error rate ≤ 0.1%（绝对目标，非回归）
//   alloc/req  ≤ +10%
//   redis/req  不得增加
//   db/req     不得增加
//
// --strict 模式下，宿主侧指标（allocations/redis/db）不可用也视为回归。
//
// 基线文件支持两种格式：
//   1. extract-summary.mjs 生成的 summary JSON（含 version/metrics 字段）
//   2. k6 --out json 原始事件流（兼容旧基线）

import fs from 'node:fs';

function parseArgs(argv) {
    const args = { strict: false, baseline: null, current: null, absolute: null };
    for (let i = 2; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--strict') args.strict = true;
        else if (a === '--baseline') args.baseline = argv[++i];
        else if (a === '--current') args.current = argv[++i];
        else if (a === '--absolute') args.absolute = argv[++i];
        else if (a === '--help' || a === '-h') {
            console.log('用法: compare-baseline.mjs --baseline <path> --current <path> [--absolute <path>] [--strict]');
            process.exit(0);
        }
    }
    if (!args.baseline || !args.current) {
        console.error('错误：必须提供 --baseline 和 --current');
        process.exit(2);
    }
    return args;
}

// 加载指标数据：支持 summary JSON 和 k6 原始事件流两种格式。
function loadMetrics(filePath) {
    if (!fs.existsSync(filePath)) {
        console.error(`错误：文件不存在: ${filePath}`);
        process.exit(2);
    }
    const raw = fs.readFileSync(filePath, 'utf8');
    const trimmed = raw.trim();

    // 检测 summary 格式（单个 JSON 对象，含 version 和 metrics）
    if (trimmed.startsWith('{') && trimmed.endsWith('}')) {
        try {
            const obj = JSON.parse(trimmed);
            if (obj.version && obj.metrics) {
                const metrics = obj.metrics;
                if (obj.rates) metrics.__rates = obj.rates;
                return metrics;
            }
        } catch { /* 不是单行 JSON，按原始事件流处理 */ }
    }

    // k6 原始事件流：每行一个 JSON 事件
    const lines = raw.split(/\r?\n/).filter(Boolean);
    const metrics = {};
    let firstTs = null;
    let lastTs = null;
    for (const line of lines) {
        let evt;
        try { evt = JSON.parse(line); } catch { continue; }
        if (evt.type !== 'Metric' || !evt.metric || !evt.data) continue;
        const m = metrics[evt.metric] || (metrics[evt.metric] = { count: 0, sum: 0, values: [] });
        const v = evt.data.value;
        m.count++;
        m.sum += v;
        m.values.push(v);
        const t = evt.data.time;
        if (t) {
            if (firstTs === null || t < firstTs) firstTs = t;
            if (lastTs === null || t > lastTs) lastTs = t;
        }
    }
    for (const m of Object.values(metrics)) {
        const sorted = [...m.values].sort((a, b) => a - b);
        m.p50 = percentile(sorted, 50);
        m.p95 = percentile(sorted, 95);
        m.p99 = percentile(sorted, 99);
        m.avg = m.count > 0 ? m.sum / m.count : 0;
        delete m.values;
    }
    const durationSeconds = firstTs && lastTs
        ? Math.max(0, (new Date(lastTs).getTime() - new Date(firstTs).getTime()) / 1000)
        : 0;
    if (durationSeconds > 0) {
        metrics.__rates = {
            duration_seconds: durationSeconds,
            iterations_per_second: metrics.iterations ? metrics.iterations.sum / durationSeconds : 0,
            http_requests_per_second: metrics.http_reqs ? metrics.http_reqs.sum / durationSeconds : 0,
        };
    }
    return metrics;
}

// 与 extract-summary.mjs / k6 内部一致：线性插值。
// 基线 summary 与当前原始事件流必须使用同一算法，否则在阈值边缘会产生假回归。
function percentile(sortedValues, p) {
    if (sortedValues.length === 0) return null;
    const idx = (p / 100) * (sortedValues.length - 1);
    const lo = Math.floor(idx);
    const hi = Math.ceil(idx);
    if (lo === hi) return sortedValues[lo];
    const frac = idx - lo;
    return sortedValues[lo] * (1 - frac) + sortedValues[hi] * frac;
}

const TREND_MAP = {
    login_ms: 'login',
    refresh_ms: 'refresh',
    friends_ms: 'friends',
    search_ms: 'search',
    notifications_ms: 'notifications',
    sessions_ms: 'sessions',
    me_ms: 'me',
    login_duration: 'login_cap',
};

function pctDelta(base, cur) {
    if (base == null || cur == null) return null;
    if (base === 0) return cur === 0 ? 0 : null;
    return (cur - base) / base;
}

function formatPct(x) {
    if (x == null) return 'n/a';
    const sign = x >= 0 ? '+' : '';
    return `${sign}${(x * 100).toFixed(1)}%`;
}

function formatMs(v) {
    if (v == null) return 'n/a';
    return `${v.toFixed(1)}ms`;
}

const ABSOLUTE_MAP = {
    read_p95_ms: { metric: 'me_ms', stat: 'p95', label: 'warmed authenticated read p95' },
    read_p99_ms: { metric: 'me_ms', stat: 'p99', label: 'warmed authenticated read p99' },
    refresh_p95_ms: { metric: 'refresh_ms', stat: 'p95', label: 'refresh p95' },
    refresh_p99_ms: { metric: 'refresh_ms', stat: 'p99', label: 'refresh p99' },
};

function emitError(file, line, message) {
    if (process.env.GITHUB_ACTIONS === 'true') {
        console.log(`::error file=${file},line=${line}::${message}`);
    } else {
        console.log(`ERROR: ${message}`);
    }
}

function emitNotice(message) {
    if (process.env.GITHUB_ACTIONS === 'true') {
        console.log(`::notice::${message}`);
    } else {
        console.log(`NOTICE: ${message}`);
    }
}

function main() {
    const args = parseArgs(process.argv);

    const baseMetrics = loadMetrics(args.baseline);
    const curMetrics = loadMetrics(args.current);

    console.log('=== 性能基线比对 ===\n');

    const regressions = [];
    const RECENT_THRESHOLDS = { p95: 0.08, p99: 0.12 };

    // 1. Trend 指标 p95/p99 回归检查
    console.log('指标                  基准 p95    当前 p95    Δ          基准 p99    当前 p99    Δ          结果');
    console.log('─'.repeat(115));
    for (const [metric, label] of Object.entries(TREND_MAP)) {
        const b = baseMetrics[metric];
        const c = curMetrics[metric];
        if (!b || !c) {
            console.log(`${label.padEnd(20)} ${(b?.p95 ?? 'n/a').toString().padStart(10)} ${(c?.p95 ?? 'n/a').toString().padStart(10)}    n/a        ${(b?.p99 ?? 'n/a').toString().padStart(10)} ${(c?.p99 ?? 'n/a').toString().padStart(10)}    n/a        跳过（缺数据）`);
            continue;
        }
        const d95 = pctDelta(b.p95, c.p95);
        const d99 = pctDelta(b.p99, c.p99);
        const ok = (d95 == null || d95 <= RECENT_THRESHOLDS.p95) && (d99 == null || d99 <= RECENT_THRESHOLDS.p99);
        console.log(`${label.padEnd(20)} ${formatMs(b.p95).padStart(10)} ${formatMs(c.p95).padStart(10)}  ${formatPct(d95).padStart(8)}  ${formatMs(b.p99).padStart(10)} ${formatMs(c.p99).padStart(10)}  ${formatPct(d99).padStart(8)}  ${ok ? '✓' : '❌'}`);
        if (!ok) {
            if (d95 != null && d95 > RECENT_THRESHOLDS.p95) regressions.push(`${label} p95 退化 ${formatPct(d95)}（阈值 +${(RECENT_THRESHOLDS.p95 * 100).toFixed(0)}%）`);
            if (d99 != null && d99 > RECENT_THRESHOLDS.p99) regressions.push(`${label} p99 退化 ${formatPct(d99)}（阈值 +${(RECENT_THRESHOLDS.p99 * 100).toFixed(0)}%）`);
        }
    }

    // 2. 错误率检查
    console.log();
    const baseErr = baseMetrics.errors?.avg ?? null;
    const curErr = curMetrics.errors?.avg ?? null;
    const ERR_ABSOLUTE_TARGET = 0.001;
    console.log(`错误率                基准 ${baseErr != null ? (baseErr * 100).toFixed(3) + '%' : 'n/a'}    当前 ${curErr != null ? (curErr * 100).toFixed(3) + '%' : 'n/a'}    目标 < 0.1%`);
    if (curErr != null && curErr > ERR_ABSOLUTE_TARGET) {
        regressions.push(`错误率 ${(curErr * 100).toFixed(3)}% 超过绝对目标 0.1%`);
    }

    // 业务迭代和 HTTP 请求不是同一个单位：一个 steady 迭代会发出多个请求。
    console.log('\n=== 负载速率（单位明确） ===');
    const baseRates = baseMetrics.__rates;
    const curRates = curMetrics.__rates;
    console.log(`iterations/s          基准 ${baseRates?.iterations_per_second?.toFixed?.(2) ?? 'n/a'}  当前 ${curRates?.iterations_per_second?.toFixed?.(2) ?? 'n/a'}`);
    console.log(`HTTP requests/s       基准 ${baseRates?.http_requests_per_second?.toFixed?.(2) ?? 'n/a'}  当前 ${curRates?.http_requests_per_second?.toFixed?.(2) ?? 'n/a'}`);

    // 3. 绝对目标检查（可选）
    let absGoals = null;
    if (args.absolute) {
        console.log('\n=== 绝对目标检查 ===');
        try {
            absGoals = JSON.parse(fs.readFileSync(args.absolute, 'utf8'));
        } catch (e) {
            console.error(`错误：无法读取绝对目标文件: ${args.absolute}`);
            process.exit(2);
        }
        for (const [goalName, target] of Object.entries(absGoals)) {
            if (goalName.startsWith('_')) continue;
            const mapping = ABSOLUTE_MAP[goalName];
            if (!mapping) continue;
            const actual = curMetrics[mapping.metric]?.[mapping.stat];
            if (actual == null) {
                console.log(`${mapping.label.padEnd(28)} n/a（无数据）`);
                continue;
            }
            const ok = actual <= target;
            const unit = goalName.endsWith('_ms') ? 'ms' : '';
            console.log(`${mapping.label.padEnd(28)} ${actual.toFixed(1)}${unit}  目标 ≤ ${target}${unit}  ${ok ? '✓' : '❌'}`);
            if (!ok) regressions.push(`${mapping.label} ${actual.toFixed(1)}${unit} 超过绝对目标 ${target}${unit}`);
        }
    }

    // 4. 宿主侧 per-request 指标
    console.log('\n=== 宿主侧 per-request 指标 ===');
    const httpReqsBase = baseMetrics.http_reqs?.sum ?? null;
    const httpReqsCur = curMetrics.http_reqs?.sum ?? null;
    const allocDeltaBase = baseMetrics.allocations_delta_bytes?.avg ?? null;
    const allocDeltaCur = curMetrics.allocations_delta_bytes?.avg ?? null;
    const redisDeltaBase = baseMetrics.redis_cmds_delta?.avg ?? null;
    const redisDeltaCur = curMetrics.redis_cmds_delta?.avg ?? null;
    const dbDeltaBase = baseMetrics.db_queries_delta?.avg ?? null;
    const dbDeltaCur = curMetrics.db_queries_delta?.avg ?? null;

    const hostAvailable = httpReqsCur != null && httpReqsCur > 0;
    const allocPerReqKbBase = (allocDeltaBase != null && httpReqsBase > 0) ? (allocDeltaBase / 1024) / httpReqsBase : null;
    const allocPerReqKbCur = (allocDeltaCur != null && httpReqsCur > 0) ? (allocDeltaCur / 1024) / httpReqsCur : null;
    const redisPerReqBase = (redisDeltaBase != null && httpReqsBase > 0) ? redisDeltaBase / httpReqsBase : null;
    const redisPerReqCur = (redisDeltaCur != null && httpReqsCur > 0) ? redisDeltaCur / httpReqsCur : null;
    const dbPerReqBase = (dbDeltaBase != null && httpReqsBase > 0) ? dbDeltaBase / httpReqsBase : null;
    const dbPerReqCur = (dbDeltaCur != null && httpReqsCur > 0) ? dbDeltaCur / httpReqsCur : null;

    const HOST_THRESHOLDS = { alloc: 0.10, redis: 0.0, db: 0.0 };

    function fmtHost(v) {
        if (v == null) return 'n/a';
        return v.toFixed(3);
    }

    for (const [label, baseVal, curVal, threshold, thresholdLabel] of [
        ['allocations/req (KB)', allocPerReqKbBase, allocPerReqKbCur, HOST_THRESHOLDS.alloc, '+10%'],
        ['redis cmds/req', redisPerReqBase, redisPerReqCur, HOST_THRESHOLDS.redis, '+0%'],
        ['db queries/req', dbPerReqBase, dbPerReqCur, HOST_THRESHOLDS.db, '+0%'],
    ]) {
        const d = pctDelta(baseVal, curVal);
        const ok = d == null || d <= threshold;
        console.log(`${label.padEnd(28)} 基准 ${fmtHost(baseVal)}  当前 ${fmtHost(curVal)}  ${d != null ? formatPct(d) : 'n/a'}  ${ok ? '✓' : '❌'}`);
        if (!ok) regressions.push(`${label} 退化 ${formatPct(d)}（阈值 ${thresholdLabel}）`);
    }

    if (!hostAvailable) {
        if (args.strict) {
            regressions.push('宿主侧指标不可用（/debug/metrics 未上报或 http_reqs 为 0），--strict 模式下视为回归');
        } else {
            console.log('  （宿主侧指标不可用：/debug/metrics 未上报或 http_reqs 为 0，跳过阻塞判定）');
        }
    }

    if (args.absolute && hostAvailable) {
        if (allocPerReqKbCur != null && absGoals?.alloc_per_req_kb != null) {
            const ok = allocPerReqKbCur <= absGoals.alloc_per_req_kb;
            console.log(`${'  [abs] allocations/req'.padEnd(28)} ${allocPerReqKbCur.toFixed(3)} KB  目标 ≤ ${absGoals.alloc_per_req_kb} KB  ${ok ? '✓' : '❌'}`);
            if (!ok) regressions.push(`allocations/request ${allocPerReqKbCur.toFixed(3)} KB 超过绝对目标 ${absGoals.alloc_per_req_kb} KB`);
        }
        if (redisPerReqCur != null && absGoals?.redis_cmds_per_req != null) {
            const ok = redisPerReqCur <= absGoals.redis_cmds_per_req;
            console.log(`${'  [abs] redis cmds/req'.padEnd(28)} ${redisPerReqCur.toFixed(3)}  目标 ≤ ${absGoals.redis_cmds_per_req}  ${ok ? '✓' : '❌'}`);
            if (!ok) regressions.push(`Redis commands/request ${redisPerReqCur.toFixed(3)} 超过绝对目标 ${absGoals.redis_cmds_per_req}`);
        }
    }

    // 5. 结论
    console.log('\n=== 结论 ===');
    if (regressions.length === 0) {
        console.log('✓ 全部通过：未检测到性能回归。');
        emitNotice('性能回归门禁通过');
        process.exit(0);
    } else {
        console.log(`❌ 检测到 ${regressions.length} 项回归：`);
        for (const r of regressions) {
            console.log(`  - ${r}`);
            emitError('tests/load/compare-baseline.mjs', 1, r);
        }
        process.exit(1);
    }
}

main();
