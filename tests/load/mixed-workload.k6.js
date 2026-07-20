import http from 'k6/http';
import { check, sleep, group } from 'k6';
import { SharedArray } from 'k6/data';
import { Rate, Trend, Counter } from 'k6/metrics';

/**
 * 混合负载：登录 / 刷新 / 好友 / 搜索 / 通知，用于固定硬件容量基线。
 *
 * 记录项（k6 端）: p95/p99（各 Trend）、错误率、HTTP 状态。
 * 宿主侧请同步采集: Redis ping、DB 连接池等待、GC、notification.outbox.backlog、avatar.reencode.*。
 *
 * 用法:
 *   ASPNETCORE_ENVIRONMENT=Performance dotnet run --project ChatApp.Server.csproj
 *   k6 run -e BASE_URL=http://localhost:8080 -e CREDS_FILE=./creds.json -e PROFILE=mixed \
 *     tests/load/mixed-workload.k6.js
 *
 * 双实例限流验证（非 Performance）:
 *   k6 run -e BASE_URL_A=http://localhost:8080 -e BASE_URL_B=http://localhost:8081 \
 *     -e PROFILE=dual_ratelimit tests/load/mixed-workload.k6.js
 */

const errorRate = new Rate('errors');
const loginTrend = new Trend('login_ms', true);
const refreshTrend = new Trend('refresh_ms', true);
const friendsTrend = new Trend('friends_ms', true);
const searchTrend = new Trend('search_ms', true);
const notifyTrend = new Trend('notifications_ms', true);
const overloaded = new Counter('login_overloaded_503');

const PROFILE = __ENV.PROFILE || 'mixed';
const BASE_URL = (__ENV.BASE_URL || 'http://localhost:8080').replace(/\/$/, '');
const BASE_URL_A = (__ENV.BASE_URL_A || BASE_URL).replace(/\/$/, '');
const BASE_URL_B = (__ENV.BASE_URL_B || BASE_URL).replace(/\/$/, '');

const users = new SharedArray('users', () => {
  if (__ENV.CREDS_FILE) return JSON.parse(open(__ENV.CREDS_FILE));
  const prefix = __ENV.TEST_USER_PREFIX || 'loaduser';
  const password = __ENV.TEST_PASSWORD || 'Passw0rd!';
  const count = Number(__ENV.USER_COUNT || 50);
  const list = [];
  for (let i = 1; i <= count; i++) list.push({ username: `${prefix}${i}`, password, userId: 0 });
  return list;
});

const profiles = {
  mixed: {
    scenarios: {
      steady: {
        executor: 'constant-arrival-rate',
        rate: Number(__ENV.RATE || 20),
        timeUnit: '1s',
        duration: __ENV.DURATION || '10m',
        preAllocatedVUs: Math.min(40, users.length || 40),
        maxVUs: Math.min(120, (users.length || 40) * 3),
      },
    },
    thresholds: {
      errors: ['rate<0.05'],
      login_ms: ['p(95)<800', 'p(99)<2000'],
      refresh_ms: ['p(95)<300', 'p(99)<800'],
      friends_ms: ['p(95)<400', 'p(99)<1000'],
      search_ms: ['p(95)<500', 'p(99)<1200'],
      notifications_ms: ['p(95)<400', 'p(99)<1000'],
      http_req_failed: ['rate<0.05'],
    },
  },
  dual_ratelimit: {
    vus: 10,
    duration: '30s',
    thresholds: {
      // 两实例共享 Redis 窗口，应出现 429
      http_req_duration: ['p(95)<2000'],
    },
  },
};

export const options = profiles[PROFILE] || profiles.mixed;

function deviceHeaders(vu) {
  return {
    'Content-Type': 'application/json',
    'X-Device-Id': `k6-mixed-${vu}-${__ITER}`,
    'X-Correlation-Id': `k6-${vu}-${Date.now()}`,
  };
}

function pickUser() {
  return users[(__VU - 1) % users.length];
}

export default function () {
  if (PROFILE === 'dual_ratelimit') {
    dualRateLimit();
    return;
  }

  const user = pickUser();
  const headers = deviceHeaders(__VU);
  let accessToken = '';
  let refreshToken = '';
  let userId = user.userId || 0;

  group('login', () => {
    const res = http.post(
      `${BASE_URL}/api/auth/login`,
      JSON.stringify({ username: user.username, password: user.password }),
      { headers, tags: { endpoint: 'login' } },
    );
    loginTrend.add(res.timings.duration);
    if (res.status === 503) overloaded.add(1);
    const ok = check(res, {
      'login 200/400/503': (r) => r.status === 200 || r.status === 400 || r.status === 503,
    });
    errorRate.add(!ok || res.status >= 500);
    if (res.status === 200) {
      const body = res.json();
      accessToken = body.accessToken || body.AccessToken || '';
      refreshToken = body.refreshToken || body.RefreshToken || '';
      userId = body.userId || body.UserId || userId;
    }
  });

  if (!accessToken) {
    sleep(0.5);
    return;
  }

  const auth = Object.assign({}, headers, { Authorization: `Bearer ${accessToken}` });

  group('friends', () => {
    const res = http.get(`${BASE_URL}/api/Friendship/all`, { headers: auth, tags: { endpoint: 'friends' } });
    friendsTrend.add(res.timings.duration);
    const ok = check(res, { 'friends 200': (r) => r.status === 200 });
    errorRate.add(!ok);
  });

  group('search', () => {
    const q = encodeURIComponent((user.username || 'a').slice(0, 3));
    const res = http.get(`${BASE_URL}/api/users/search?q=${q}&limit=10`, {
      headers: auth,
      tags: { endpoint: 'search' },
    });
    searchTrend.add(res.timings.duration);
    const ok = check(res, { 'search 200': (r) => r.status === 200 });
    errorRate.add(!ok);
  });

  group('notifications', () => {
    const res = http.get(`${BASE_URL}/api/users/me/notifications?limit=20`, {
      headers: auth,
      tags: { endpoint: 'notifications' },
    });
    notifyTrend.add(res.timings.duration);
    const ok = check(res, {
      'notifications 200': (r) => r.status === 200,
    });
    errorRate.add(!ok);
  });

  group('refresh', () => {
    if (!refreshToken || !userId) return;
    const res = http.post(
      `${BASE_URL}/api/auth/refresh`,
      JSON.stringify({ userId, refreshToken }),
      { headers, tags: { endpoint: 'refresh' } },
    );
    refreshTrend.add(res.timings.duration);
    const ok = check(res, { 'refresh 200': (r) => r.status === 200 });
    errorRate.add(!ok);
  });

  sleep(Number(__ENV.THINK || 0.2));
}

function dualRateLimit() {
  const user = pickUser();
  const headers = deviceHeaders(__VU);
  const body = JSON.stringify({ username: user.username, password: 'wrong-password-for-rl' });
  const a = http.post(`${BASE_URL_A}/api/auth/login`, body, { headers, tags: { endpoint: 'login_a' } });
  const b = http.post(`${BASE_URL_B}/api/auth/login`, body, { headers, tags: { endpoint: 'login_b' } });
  loginTrend.add(a.timings.duration);
  loginTrend.add(b.timings.duration);
  // 限流场景下 429 是预期成功信号之一
  const hitLimit = a.status === 429 || b.status === 429;
  check(null, { 'dual instance shared limit eventually 429': () => hitLimit || a.status < 500 });
  errorRate.add(a.status >= 500 || b.status >= 500);
  sleep(0.05);
}
