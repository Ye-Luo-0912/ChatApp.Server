#!/usr/bin/env node
// 从 k6 --out json=<file> 的原始事件流提取压缩 summary JSON，用于基线存储。
//
// 用法：
//   node extract-summary.mjs --input <raw.json> --output <summary.json> \
//                            [--commit <sha>] [--runner <name>] [--runtime <info>]
//
// 输出格式（与 compare-baseline.mjs 兼容）：
//   {
//     "version": "1.0",
//     "commit": "...",
//     "runner": "...",
//     "generated_at": "ISO8601",
//     "runtime": "...",
//     "metrics": {
//       "me_ms": { "p50": 8.2, "p95": 15.4, "p99": 24.1, "avg": 7.5, "sum": 7500, "count": 1000 },
//       "http_reqs": { "sum": 18000, "count": 18000, ... },
//       ...
//     }
//     "rates": {
//       "duration_seconds": 900,
//       "iterations_per_second": 20,
//       "http_requests_per_second": 100
//     }
//   }
//
// 设计目标：
//   - 原始事件流可能数 MB（15 分钟 steady ~3 万事件），summary 仅几 KB
//   - 避免每日 nightly 推送大量逐请求事件污染 git 历史
//   - 原始事件流仍作为 Actions artifact 保存 90 天

import fs from 'node:fs';
import { execSync } from 'node:child_process';

function parseArgs(argv) {
    const args = { input: null, output: null, commit: null, runner: null, runtime: null };
    for (let i = 2; i < argv.length; i++) {
        const a = argv[i];
        if (a === '--input') args.input = argv[++i];
        else if (a === '--output') args.output = argv[++i];
        else if (a === '--commit') args.commit = argv[++i];
        else if (a === '--runner') args.runner = argv[++i];
        else if (a === '--runtime') args.runtime = argv[++i];
        else if (a === '--help' || a === '-h') {
            console.log('用法: extract-summary.mjs --input <raw.json> --output <summary.json> [--commit <sha>] [--runner <name>] [--runtime <info>]');
            process.exit(0);
        }
    }
    if (!args.input || !args.output) {
        console.error('错误：必须提供 --input 和 --output');
        process.exit(2);
    }
    return args;
}

function percentile(sortedValues, p) {
    if (sortedValues.length === 0) return null;
    // 与 k6 内部 percentile 算法一致：线性插值
    const idx = (p / 100) * (sortedValues.length - 1);
    const lo = Math.floor(idx);
    const hi = Math.ceil(idx);
    if (lo === hi) return sortedValues[lo];
    const frac = idx - lo;
    return sortedValues[lo] * (1 - frac) + sortedValues[hi] * frac;
}

function isMetricPoint(evt) {
    if (!evt || !evt.metric || !evt.data || typeof evt.data !== 'object') return false;

    // k6 1.x emitted { type: "Metric", ... }, while k6 2.x emits
    // { metric: "...", type: "Point", data: { value, time, ... } }.
    // Some intermediate versions put the point type under data.type.
    return evt.type === 'Metric' || evt.type === 'Point' || evt.data.type === 'Point';
}

function main() {
    const args = parseArgs(process.argv);

    if (!fs.existsSync(args.input)) {
        console.error(`错误：输入文件不存在: ${args.input}`);
        process.exit(2);
    }

    const raw = fs.readFileSync(args.input, 'utf8');
    const lines = raw.split(/\r?\n/).filter(Boolean);

    // 聚合：metric -> { count, sum, values[], thresholds?(仅 GAUGE 取最后值) }
    const metrics = {};
    let firstTs = null;
    let lastTs = null;

    for (const line of lines) {
        let evt;
        try { evt = JSON.parse(line); } catch { continue; }
        if (!isMetricPoint(evt)) continue;

        const m = metrics[evt.metric] || (metrics[evt.metric] = { count: 0, sum: 0, values: [] });
        const v = evt.data.value;
        const t = evt.data.time;
        if (Number.isFinite(v)) {
            m.count++;
            m.sum += v;
            m.values.push(v);
        }
        if (t) {
            if (firstTs === null || t < firstTs) firstTs = t;
            if (lastTs === null || t > lastTs) lastTs = t;
        }
    }

    // 计算分位数并裁剪 values
    const summary = {};
    for (const [name, m] of Object.entries(metrics)) {
        const sorted = [...m.values].sort((a, b) => a - b);
        const entry = {
            p50: percentile(sorted, 50),
            p95: percentile(sorted, 95),
            p99: percentile(sorted, 99),
            avg: m.count > 0 ? m.sum / m.count : 0,
            sum: m.sum,
            count: m.count,
        };
        // 移除 null（仅当 values 为空，比如纯 GAUGE 没有 value 字段）
        for (const k of Object.keys(entry)) {
            if (entry[k] === null) delete entry[k];
        }
        summary[name] = entry;
    }

    let commit = args.commit;
    if (!commit) {
        try {
            commit = execSync('git rev-parse HEAD', { encoding: 'utf8' }).trim();
        } catch { commit = 'unknown'; }
    }

    const output = {
        version: '1.0',
        commit,
        runner: args.runner || 'ubuntu-24.04',
        generated_at: new Date().toISOString(),
        runtime: args.runtime || undefined,
        first_event_ts: firstTs || undefined,
        last_event_ts: lastTs || undefined,
        metrics: summary,
    };

    // 明确区分业务迭代速率和 HTTP 请求速率：一次迭代通常包含多个请求，
    // 不能用 http_reqs 代替 iterations 来解释 steady 负载。
    const durationSeconds = firstTs && lastTs
        ? Math.max(0, (new Date(lastTs).getTime() - new Date(firstTs).getTime()) / 1000)
        : 0;
    if (durationSeconds > 0) {
        output.rates = {
            duration_seconds: durationSeconds,
            iterations_per_second: summary.iterations ? summary.iterations.sum / durationSeconds : 0,
            http_requests_per_second: summary.http_reqs ? summary.http_reqs.sum / durationSeconds : 0,
        };
    }

    // 移除 undefined 字段
    for (const k of Object.keys(output)) {
        if (output[k] === undefined) delete output[k];
    }

    fs.writeFileSync(args.output, JSON.stringify(output, null, 2) + '\n');

    const inSize = fs.statSync(args.input).size;
    const outSize = fs.statSync(args.output).size;
    const ratio = inSize > 0 ? ((outSize / inSize) * 100).toFixed(1) : '0';
    console.log(`✓ summary 已生成: ${args.output}`);
    console.log(`  输入: ${args.input} (${(inSize / 1024).toFixed(1)} KB)`);
    console.log(`  输出: ${(outSize / 1024).toFixed(1)} KB (${ratio}% of input)`);
    console.log(`  指标数量: ${Object.keys(summary).length}`);
    console.log(`  commit: ${commit}`);
}

main();
