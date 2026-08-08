#!/usr/bin/env node
// Aggregate repeated endpoint/Stage-Gate summaries.  Percentiles are first
// computed per run by extract-summary.mjs, then the run-level median is used
// for the gate.  Min/max and MAD are retained as a noise interval so a gate
// result is not presented as more precise than the hardware allows.

import fs from 'node:fs';

function parseArgs(argv) {
  const args = { inputs: [], output: null, runner: null, runtime: null };
  for (let i = 2; i < argv.length; i++) {
    const value = argv[i];
    if (value === '--input') args.inputs.push(argv[++i]);
    else if (value === '--output') args.output = argv[++i];
    else if (value === '--runner') args.runner = argv[++i];
    else if (value === '--runtime') args.runtime = argv[++i];
    else if (value === '--help' || value === '-h') {
      console.log('用法: aggregate-runs.mjs --input <summary> --input <summary> --output <summary> [--runner <id>]');
      process.exit(0);
    } else {
      console.error(`未知参数: ${value}`);
      process.exit(2);
    }
  }
  if (args.inputs.length < 2 || !args.output) {
    console.error('至少需要两个 --input 和一个 --output');
    process.exit(2);
  }
  return args;
}

function median(values) {
  const sorted = values.filter(Number.isFinite).sort((a, b) => a - b);
  if (sorted.length === 0) return null;
  const middle = (sorted.length - 1) / 2;
  const lo = Math.floor(middle);
  const hi = Math.ceil(middle);
  return lo === hi ? sorted[lo] : (sorted[lo] + sorted[hi]) / 2;
}

function noise(values) {
  const numbers = values.filter(Number.isFinite);
  if (numbers.length === 0) return null;
  const center = median(numbers);
  const deviations = numbers.map(value => Math.abs(value - center));
  return {
    min: Math.min(...numbers),
    max: Math.max(...numbers),
    median: center,
    mad: median(deviations),
  };
}

function readSummary(path) {
  if (!fs.existsSync(path)) throw new Error(`输入不存在: ${path}`);
  const summary = JSON.parse(fs.readFileSync(path, 'utf8'));
  if (!summary || typeof summary.metrics !== 'object')
    throw new Error(`不是 extract-summary 格式: ${path}`);
  return summary;
}

function main() {
  const args = parseArgs(process.argv);
  const runs = args.inputs.map(readSummary);
  const runners = [...new Set(runs.map(run => run.runner).filter(Boolean))];
  if (runners.length > 1)
    throw new Error(`重复运行的 runner 不一致: ${runners.join(', ')}`);
  if (args.runner && runners.length === 1 && args.runner !== runners[0])
    throw new Error(`runner 不匹配: expected=${args.runner}, actual=${runners[0]}`);

  const metricNames = [...new Set(runs.flatMap(run => Object.keys(run.metrics)))].sort();
  const metrics = {};
  const noiseIntervals = {};

  for (const name of metricNames) {
    const points = runs.map(run => run.metrics[name]).filter(point => point && typeof point === 'object');
    if (points.length === 0) continue;

    const entry = {
      p50: median(points.map(point => Number(point.p50))),
      p95: median(points.map(point => Number(point.p95))),
      p99: median(points.map(point => Number(point.p99))),
      avg: median(points.map(point => Number(point.avg))),
      // Sums describe one representative run, so use the run median.  Counts
      // are safe to add because they only enforce the minimum sample gate.
      sum: median(points.map(point => Number(point.sum))),
      count: points.reduce((total, point) => total + (Number(point.count) || 0), 0),
    };
    for (const key of Object.keys(entry)) {
      if (entry[key] === null || !Number.isFinite(entry[key])) delete entry[key];
    }
    metrics[name] = entry;

    const interval = {};
    for (const stat of ['p50', 'p95', 'p99', 'avg']) {
      const value = noise(points.map(point => Number(point[stat])));
      if (value) interval[stat] = value;
    }
    if (Object.keys(interval).length > 0) noiseIntervals[name] = interval;
  }

  const rates = {};
  for (const key of ['duration_seconds', 'iterations_per_second', 'http_requests_per_second']) {
    const value = median(runs.map(run => Number(run.rates?.[key])));
    if (value !== null) rates[key] = value;
  }

  const output = {
    version: '1.1',
    aggregation: 'median-of-runs',
    runs: runs.length,
    runner: args.runner || runners[0] || 'unknown',
    commits: [...new Set(runs.map(run => run.commit).filter(Boolean))],
    generated_at: new Date().toISOString(),
    runtime: args.runtime || runs[0].runtime,
    metrics,
    noise_intervals: noiseIntervals,
  };
  if (Object.keys(rates).length > 0) output.rates = rates;

  fs.writeFileSync(args.output, JSON.stringify(output, null, 2) + '\n');
  console.log(`✓ ${runs.length} 次运行已聚合: ${args.output}`);
  for (const [name, interval] of Object.entries(noiseIntervals)) {
    const p95 = interval.p95;
    if (p95) console.log(`  ${name} p95 median=${p95.median} noise=[${p95.min}, ${p95.max}] MAD=${p95.mad}`);
  }
}

try {
  main();
} catch (error) {
  console.error(`错误: ${error instanceof Error ? error.message : error}`);
  process.exit(1);
}
