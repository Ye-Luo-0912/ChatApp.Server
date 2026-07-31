#!/usr/bin/env node
// 为稳态 k6 负载生成会话。userId 作为十进制字符串保存，避免 JS Number 精度丢失；
// 每个会话同时保存登录时使用的 InstallationId，refresh 必须复用该值。
import http from 'node:http';
import fs from 'node:fs';

const host = process.env.TOKEN_HOST || '127.0.0.1';
const port = Number(process.env.TOKEN_PORT || 5088);
const count = Number(process.env.TOKEN_COUNT || 50);
const minSuccess = Number(process.env.TOKEN_MIN_SUCCESS || Math.max(1, Math.floor(count * 0.8)));
const usernamePrefix = process.env.TEST_USER_PREFIX || 'loaduser';
const password = process.env.TEST_PASSWORD || 'Passw0rd!';
const outputPath = process.env.TOKENS_OUTPUT || 'tokens.json';

function installationId(index) {
  return `perf-installation-${String(index).padStart(8, '0')}`;
}

function login(index) {
  return new Promise((resolve) => {
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
            const userId = responseBody.match(/"userId"\s*:\s*(\d+)/i)?.[1];
            if (response.statusCode === 200 && parsed.accessToken && userId) {
              resolve({
                accessToken: parsed.accessToken,
                refreshToken: parsed.refreshToken || '',
                deviceCredential: parsed.deviceCredential || '',
                userId,
                deviceId,
              });
              return;
            }
          } catch {
            // Count as a failed login below.
          }
          resolve(null);
        });
      });
    request.on('error', () => resolve(null));
    request.write(body);
    request.end();
  });
}

const attempts = await Promise.all(Array.from({ length: count }, (_, i) => login(i + 1)));
const tokens = attempts.filter((entry) => entry !== null);
fs.writeFileSync(outputPath, JSON.stringify(tokens));
console.log(`Generated ${tokens.length}/${count} preset sessions`);
process.exit(tokens.length >= minSuccess ? 0 : 1);
