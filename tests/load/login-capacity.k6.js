import http from 'k6/http';
import { check, sleep } from 'k6';
import { SharedArray } from 'k6/data';
import { Rate, Trend } from 'k6/metrics';

/**
 * 登录容量测试：只压 BCrypt 登录路径。
 * 注意：默认 auth-login 限流为每 IP 每分钟 10 次；容量结论请在 Performance 环境
 * 调高限流或预置 Token，并与限流专项测试分开执行。
 *
 *   k6 run -e BASE_URL=http://localhost:8080 -e CREDS_FILE=./creds.json -e PROFILE=smoke tests/load/login-capacity.k6.js
 */

const errorRate = new Rate('errors');
const loginDuration = new Trend('login_duration', true);

const PROFILE = __ENV.PROFILE || 'smoke';
const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';

const users = new SharedArray('users', () => {
  if (__ENV.CREDS_FILE) return JSON.parse(open(__ENV.CREDS_FILE));
  const prefix = __ENV.TEST_USER_PREFIX || 'loaduser';
  const password = __ENV.TEST_PASSWORD || 'password';
  const count = Number(__ENV.USER_COUNT || 50);
  return Array.from({ length: count }, (_, i) => ({
    username: `${prefix}${i + 1}`,
    password,
  }));
});

const profiles = {
  smoke: { vus: Math.min(5, users.length || 5), duration: '1m' },
  capacity: {
    stages: [
      { duration: '30s', target: 20 },
      { duration: '2m', target: 50 },
      { duration: '1m', target: 0 },
    ],
  },
};

export const options = {
  ...profiles[PROFILE],
  thresholds: {
    http_req_failed: ['rate<0.05'],
    login_duration: ['p(95)<800'],
    errors: ['rate<0.05'],
  },
};

export default function () {
  const user = users[(__VU - 1) % users.length];
  const start = Date.now();
  const res = http.post(
    `${BASE_URL}/api/auth/login`,
    JSON.stringify({ username: user.username, password: user.password }),
    {
      headers: {
        'Content-Type': 'application/json',
        'X-Device-Id': `k6-login-${__VU}`,
        'X-Correlation-Id': `login-${__VU}-${__ITER}`,
      },
    },
  );
  loginDuration.add(Date.now() - start);
  const ok = check(res, { 'login 200': (r) => r.status === 200 });
  errorRate.add(!ok);
  sleep(0.5);
}
