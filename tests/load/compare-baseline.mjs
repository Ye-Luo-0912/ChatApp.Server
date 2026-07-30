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
//
// 绝对目标（可选，--absolute 指向 absolute-goals.json）：
//   {
//     "read_p95_ms": 50, "read_p99_ms": 150,
//     "refresh_p95_ms": 100, "refresh_p99_ms": 250,
//     "error_rate": 0.001, "alloc_per_req_kb": 8,
//     "redis_cmds_per_req": 0.2
//   }
//
// 宿主侧指标（allocations/redis/db queries）当前从 k6 JSON 的 custom metrics 读取，
// 若不存在则跳过该维度（info-only，不阻塞）。

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

// k6 --out json= 输出每行一个 JSON 事件。我们只关心 metric 事件的 values。
function loadK6Json(filePath) {
    if (!fs.existsSync(filePath)) {
        console.error(`错误：k6 输出文件不存在: ${filePath}`);
        process.exit(2);
    }
    const lines = fs.readFileSync(filePath, 'utf8').split(/\r?\n/).filter(Boolean);
    const metrics = {}; // name -> { count, sum, min, max, values: [] }
    for (const line of lines) {
        let evt;
        try { evt = JSON.parse(line); } catch { continue; }
        if (evt.type !== 'Metric' || !evt.metric || !evt.data) continue;
        const m = metrics[evt.metric] || (metrics[evt.metric] = { count: 0, sum: 0, min: Infinity, max: 0, values: [] });
        const v = evt.data.value;
        m.count++;
        m.sum += v;
        if (v < m.min) m.min = v;
        if (v > m.max) m.max = v;
        m.values.push(v);
    }
    return metrics;
}

function percentile(sortedValues, p) {
    if (sortedValues.length === 0) return null;
    const idx = Math.ceil((p / 100) * sortedValues.length) - 1;
    return sortedValues[Math.max(0, Math.min(sortedValues.length - 1, idx))];
}

// 从 metrics 提取 summary：p95/p99/avg/count
function summarize(metrics) {
    const summary = {};
    for (const [name, m] of Object.entries(metrics)) {
        const sorted = [...m.values].sort((a, b) => a - b);
        summary[name] = {
            p95: percentile(sorted, 95),
            p99: percentile(sorted, 99),
            avg: m.count > 0 ? m.sum / m.count : 0,
            count: m.count,
        };
    }
    return summary;
}

// k6 Trend 指标名称 → 友好名映射
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
    if (base === 0) return cur === 0 ? 0 : null; // 基线为 0 时无法比较
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

// 绝对目标配置名 → summary 指标名映射
const ABSOLUTE_MAP = {
    read_p95_ms: { metric: 'me_ms', stat: 'p95', label: 'warmed authenticated read p95' },
    read_p99_ms: { metric: 'me_ms', stat: 'p99', label: 'warmed authenticated read p99' },
    refresh_p95_ms: { metric: 'refresh_ms', stat: 'p95', label: 'refresh p95' },
    refresh_p99_ms: { metric: 'refresh_ms', stat: 'p99', label: 'refresh p99' },
};

// GitHub Actions ::error:: 注解
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

    const baseSummary = summarize(loadK6Json(args.baseline));
    const curSummary = summarize(loadK6Json(args.current));

    console.log('=== 性能基线比对 ===\n');

    // 1. 回归检查：Trend 指标 p95/p99
    const regressions = [];
    const RECENT_THRESHOLDS = { p95: 0.08, p99: 0.12 }; // +8% / +12%

    console.log('指标                  基准 p95    当前 p95    Δ          基准 p99    当前 p99    Δ          结果');
    console.log('─'.repeat(115));
    for (const [metric, label] of Object.entries(TREND_MAP)) {
        const b = baseSummary[metric];
        const c = curSummary[metric];
        if (!b || !c) {
            console.log(`${label.padEnd(20)} ${(b?.p95 ?? 'n/a').toString().padStart(10)} ${(c?.p95 ?? 'n/a').toString().padStart(10)}    n/a        ${(b?.p99 ?? 'n/a').toString().padStart(10)} ${(c?.p99 ?? 'n/a').toString().padStart(10)}    n/a        跳过（缺数据）`);
            continue;
        }
        const d95 = pctDelta(b.p95, c.p95);
        const d99 = pctDelta(b.p99, c.p99);
        const r95 = d95 == null ? 'n/a' : (d95 > RECENT_THRESHOLDS.p95 ? '❌' : '✓');
        const r99 = d99 == null ? 'n/a' : (d99 > RECENT_THRESHOLDS.p99 ? '❌' : '✓');
        const ok = (d95 == null || d95 <= RECENT_THRESHOLDS.p95) && (d99 == null || d99 <= RECENT_THRESHOLDS.p99);
        console.log(`${label.padEnd(20)} ${formatMs(b.p95).padStart(10)} ${formatMs(c.p95).padStart(10)}  ${formatPct(d95).padStart(8)}  ${formatMs(b.p99).padStart(10)} ${formatMs(c.p99).padStart(10)}  ${formatPct(d99).padStart(8)}  ${ok ? '✓' : '❌'}`);
        if (!ok) {
            if (d95 != null && d95 > RECENT_THRESHOLDS.p95) regressions.push(`${label} p95 退化 ${formatPct(d95)}（阈值 +${(RECENT_THRESHOLDS.p95 * 100).toFixed(0)}%）`);
            if (d99 != null && d99 > RECENT_THRESHOLDS.p99) regressions.push(`${label} p99 退化 ${formatPct(d99)}（阈值 +${(RECENT_THRESHOLDS.p99 * 100).toFixed(0)}%）`);
        }
    }

    // 2. 错误率检查（Rate 指标 'errors' 的 avg 即错误率）
    console.log();
    const baseErr = baseSummary.errors?.avg ?? null;
    const curErr = curSummary.errors?.avg ?? null;
    const ERR_ABSOLUTE_TARGET = 0.001; // < 0.1%
    console.log(`错误率                基准 ${baseErr != null ? (baseErr * 100).toFixed(3) + '%' : 'n/a'}    当前 ${curErr != null ? (curErr * 100).toFixed(3) + '%' : 'n/a'}    目标 < 0.1%`);
    if (curErr != null && curErr > ERR_ABSOLUTE_TARGET) {
        regressions.push(`错误率 ${(curErr * 100).toFixed(3)}% 超过绝对目标 0.1%`);
    }

    // 3. 绝对目标检查（可选）
    if (args.absolute) {
        console.log('\n=== 绝对目标检查 ===');
        let absGoals;
        try {
            absGoals = JSON.parse(fs.readFileSync(args.absolute, 'utf8'));
        } catch (e) {
            console.error(`错误：无法读取绝对目标文件: ${args.absolute}`);
            process.exit(2);
        }
        for (const [goalName, target] of Object.entries(absGoals)) {
            if (goalName.startsWith('_')) continue; // 元数据字段
            const mapping = ABSOLUTE_MAP[goalName];
            if (!mapping) {
                console.log(`${goalName.padEnd(20)} 跳过（未映射）`);
                continue;
            }
            const actual = curSummary[mapping.metric]?.[mapping.stat];
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

    // 4. 宿主侧指标（allocations/redis/db queries）—当前仅 info，不阻塞
    console.log('\n=== 宿主侧指标（info-only，不阻塞）===');
    const hostMetrics = ['allocations_per_req_kb', 'redis_cmds_per_req', 'db_queries_per_req'];
    for (const hm of hostMetrics) {
        const b = baseSummary[hm]?.avg;
        const c = curSummary[hm]?.avg;
        if (b == null && c == null) {
            console.log(`${hm.padEnd(28)} n/a（k6 未上报，需宿主端 /debug/metrics 端点）`);
        } else {
            console.log(`${hm.padEnd(28)} 基准 ${b ?? 'n/a'}  当前 ${c ?? 'n/a'}`);
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
