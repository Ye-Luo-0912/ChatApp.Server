#!/usr/bin/env node
// 为稳态 k6 负载生成会话。userId 作为十进制字符串保存，避免 JS Number 精度丢失；
// 每个会话同时保存登录时使用的 InstallationId，refresh 必须复用该值。
import http from 'node:http';
import fs from 'node:fs';

function readPositiveInteger(name, fallback) {
  const raw = process.env[name];
  if (raw === undefined || raw === '') return fallback;

  const value = Number(raw);
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new Error(name + ' must be a positive integer');
  }
  return value;
}

const host = process.env.TOKEN_HOST || '127.0.0.1';
const port = readPositiveInteger('TOKEN_PORT', 5088);
const count = readPositiveInteger('TOKEN_COUNT', 50);
const minSuccess = readPositiveInteger('TOKEN_MIN_SUCCESS', Math.max(1, Math.floor(count * 0.8)));
const concurrency = Math.min(count, readPositiveInteger('TOKEN_CONCURRENCY', 4));
const requestTimeoutMs = readPositiveInteger('TOKEN_REQUEST_TIMEOUT_MS', 15000);
const usernamePrefix = process.env.TEST_USER_PREFIX || 'loaduser';
const password = process.env.TEST_PASSWORD || 'Passw0rd!';
const outputPath = process.env.TOKENS_OUTPUT || 'tokens.json';
const failureCounts = new Map();

if (minSuccess > count) {
  throw new Error('TOKEN_MIN_SUCCESS cannot exceed TOKEN_COUNT');
}

function recordFailure(reason) {
  failureCounts.set(reason, (failureCounts.get(reason) || 0) + 1);
}

function installationId(index) {
  return `perf-installation-${String(index).padStart(8, '0')}`;
}

function login(index) {
  return new Promise((resolve) => {
    let settled = false;
    const finish = (session, failureReason) => {
      if (settled) return;
      settled = true;
      if (failureReason) recordFailure(failureReason);
      resolve(session);
    };

    const deviceId = installationId(index);
    const body = JSON.stringify({ username: `${usernamePrefix}${index}`, password });
    const request = http.request(
      {
        hostname: host,
        port,
        path: '/api/auth/login',
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-Installation-Id': deviceId,
          'Content-Length': Buffer.byteLength(body),
        },
      },
      (response) => {
        let responseBody = '';
        response.on('data', (chunk) => { responseBody += chunk; });
        response.on('end', () => {
          try {
            const parsed = JSON.parse(responseBody);
            // Do not read userId through parsed JSON: TSID values can exceed Number.MAX_SAFE_INTEGER.
            const userId = responseBody.match(/"userId"\s*:\s*"?(\d+)"?/i)?.[1];
            if (response.statusCode === 200 && parsed.accessToken && userId) {
              finish({
                accessToken: parsed.accessToken,
                refreshToken: parsed.refreshToken || '',
                deviceCredential: parsed.deviceCredential || '',
                userId,
                deviceId,
              });
              return;
            }
          } catch {
            finish(null, `http_${response.statusCode || 0}_invalid_json`);
            return;
          }
          finish(
            null,
            response.statusCode === 200
              ? 'http_200_invalid_payload'
              : `http_${response.statusCode || 0}`);
        });
      });
    request.setTimeout(requestTimeoutMs, () => request.destroy(new Error('request timeout')));
    request.on('error', (error) => finish(null, `network_${error.code || 'error'}`));
    request.write(body);
    request.end();
  });
}

const attempts = new Array(count);
let nextIndex = 1;
async function worker() {
  while (true) {
    const index = nextIndex++;
    if (index > count) return;
    attempts[index - 1] = await login(index);
  }
}

await Promise.all(Array.from({ length: concurrency }, () => worker()));
const tokens = attempts.filter((entry) => entry !== null);
fs.writeFileSync(outputPath, JSON.stringify(tokens));
console.log(`Generated ${tokens.length}/${count} preset sessions with concurrency ${concurrency}`);
if (failureCounts.size > 0) {
  const summary = [...failureCounts.entries()]
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([reason, failures]) => `${reason}=${failures}`)
    .join(', ');
  console.error(`Preset session failures: ${summary}`);
}
process.exit(tokens.length >= minSuccess ? 0 : 1);
