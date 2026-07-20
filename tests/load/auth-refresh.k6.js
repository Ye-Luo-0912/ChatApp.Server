import http from 'k6/http';
import { check, sleep, group } from 'k6';
import { SharedArray } from 'k6/data';
import { Rate, Trend } from 'k6/metrics';

/**
 * 认证压测（每 VU 独立用户 / 设备 / 刷新令牌）
 *
 * 准备凭证 JSON（数组）:
 *   [{ "username":"u1","password":"p","userId":1 }, ...]
 *
 * 用法:
 *   k6 run -e BASE_URL=http://localhost:8080 -e CREDS_FILE=./creds.json -e PROFILE=smoke tests/load/auth-refresh.k6.js
 *   k6 run -e PROFILE=refresh_race tests/load/auth-refresh.k6.js   # 故意共享令牌的竞争场景
 *   k6 run -e PROFILE=bruteforce tests/load/auth-refresh.k6.js     # 暴力登录限流场景
 */

const errorRate = new Rate('errors');
const loginDuration = new Trend('login_duration', true);
const refreshDuration = new Trend('refresh_duration', true);
const authzDuration = new Trend('authz_duration', true);
const friendsDuration = new Trend('friends_duration', true);
const searchDuration = new Trend('search_duration', true);

const PROFILE = __ENV.PROFILE || 'smoke';
const BASE_URL = __ENV.BASE_URL || 'http://localhost:8080';

const users = new SharedArray('users', () => {
  if (__ENV.CREDS_FILE) {
    return JSON.parse(open(__ENV.CREDS_FILE));
  }
  // 回退：用 TEST_USER_PREFIX + VU 推导（需事先批量注册）
  const prefix = __ENV.TEST_USER_PREFIX || 'loaduser';
  const password = __ENV.TEST_PASSWORD || 'password';
  const count = Number(__ENV.USER_COUNT || 50);
  const list = [];
  for (let i = 1; i <= count; i++) {
    list.push({ username: `${prefix}${i}`, password, userId: 0 });
  }
  return list;
});

const profiles = {
  smoke: {
    vus: Math.min(5, users.length || 5),
    duration: '1m',
  },
  soak: {
    scenarios: {
      steady: {
        executor: 'constant-arrival-rate',
        rate: 40,
        timeUnit: '1s',
        duration: '30m',
        preAllocatedVUs: Math.min(40, users.length || 40),
        maxVUs: Math.min(80, (users.length || 40) * 2),
      },
    },
  },
  spike: {
    stages: [
      { duration: '30s', target: 10 },
      { duration: '1m', target: Math.min(100, users.length || 50) },
      { duration: '2m', target: Math.min(100, users.length || 50) },
      { duration: '30s', target: 10 },
    ],
  },
  // 故意共享同一刷新令牌，验证 CAS 互斥（期望大量失败）
  refresh_race: {
    vus: 50,
    duration: '30s',
  },
  // 暴力登录：同一 IP 打同一账户，验证限流
  bruteforce: {
    vus: 20,
    duration: '1m',
  },
};

export const options = {
  ...profiles[PROFILE],
  thresholds:
    PROFILE === 'refresh_race' || PROFILE === 'bruteforce'
      ? {
          // 竞争/暴力场景不强制成功率
          http_req_duration: ['p(95)<500'],
        }
      : {
          http_req_failed: ['rate<0.01'],
          http_req_duration: ['p(95)<100', 'p(99)<250'],
          errors: ['rate<0.01'],
          login_duration: ['p(95)<500'],
          refresh_duration: ['p(95)<100', 'p(99)<250'],
          authz_duration: ['p(95)<100', 'p(99)<250'],
          friends_duration: ['p(95)<100', 'p(99)<250'],
          search_duration: ['p(95)<150', 'p(99)<300'],
        },
};

function deviceHeaders(vu) {
  return {
    'Content-Type': 'application/json',
    'X-Device-Id': `k6-vu-${vu}-device-00123456`,
  };
}

function pickUser(vu) {
  return users[(vu - 1) % users.length];
}

export function setup() {
  if (PROFILE === 'refresh_race') {
    const u = pickUser(1);
    const res = http.post(
      `${BASE_URL}/api/auth/login`,
      JSON.stringify({ username: u.username, password: u.password }),
      { headers: deviceHeaders(1) },
    );
    check(res, { 'race setup login': (r) => r.status === 200 });
    const body = res.json();
    return {
      shared: {
        accessToken: body.accessToken || body.AccessToken,
        refreshToken: body.refreshToken || body.RefreshToken,
        userId: body.userId || body.UserId || u.userId,
      },
    };
  }
  return {};
}

export default function (data) {
  if (PROFILE === 'bruteforce') {
    const u = pickUser(1);
    const start = Date.now();
    const res = http.post(
      `${BASE_URL}/api/auth/login`,
      JSON.stringify({ username: u.username, password: 'wrong-password-!!!!' }),
      { headers: deviceHeaders(__VU) },
    );
    loginDuration.add(Date.now() - start);
    check(res, { 'bruteforce blocked or fail': (r) => r.status === 400 || r.status === 429 });
    sleep(0.2);
    return;
  }

  if (PROFILE === 'refresh_race') {
    const start = Date.now();
    const res = http.post(
      `${BASE_URL}/api/auth/refresh-token`,
      JSON.stringify({
        userId: data.shared.userId,
        refreshToken: data.shared.refreshToken,
      }),
      { headers: deviceHeaders(1) }, // 同设备抢同一 RT
    );
    refreshDuration.add(Date.now() - start);
    // 仅一次成功，其余失败是预期
    check(res, { 'race refresh responded': (r) => r.status === 200 || r.status === 400 });
    sleep(0.05);
    return;
  }

  const user = pickUser(__VU);
  const headers = deviceHeaders(__VU);

  let accessToken;
  let refreshToken;
  let userId = user.userId;

  group('login', () => {
    const start = Date.now();
    const res = http.post(
      `${BASE_URL}/api/auth/login`,
      JSON.stringify({ username: user.username, password: user.password }),
      { headers },
    );
    loginDuration.add(Date.now() - start);
    const ok = check(res, { 'login ok': (r) => r.status === 200 });
    errorRate.add(!ok);
    if (ok) {
      const body = res.json();
      accessToken = body.accessToken || body.AccessToken;
      refreshToken = body.refreshToken || body.RefreshToken;
      userId = body.userId || body.UserId || userId;
    }
  });

  if (!accessToken) {
    sleep(1);
    return;
  }

  const authHeaders = { ...headers, Authorization: `Bearer ${accessToken}` };

  group('authz me', () => {
    const start = Date.now();
    const res = http.get(`${BASE_URL}/api/users/me`, { headers: authHeaders });
    authzDuration.add(Date.now() - start);
    const ok = check(res, { 'me ok': (r) => r.status === 200 });
    errorRate.add(!ok);
  });

  group('friends page', () => {
    const start = Date.now();
    const res = http.get(`${BASE_URL}/api/Friendship/all?limit=50`, { headers: authHeaders });
    friendsDuration.add(Date.now() - start);
    const ok = check(res, { 'friends page ok': (r) => r.status === 200 });
    errorRate.add(!ok);
  });

  group('search', () => {
    const start = Date.now();
    const res = http.get(`${BASE_URL}/api/Friendship/search?searchTerm=te&limit=20`, {
      headers: authHeaders,
    });
    searchDuration.add(Date.now() - start);
    check(res, { 'search ok or empty': (r) => r.status === 200 || r.status === 400 });
  });

  group('refresh', () => {
    const start = Date.now();
    const res = http.post(
      `${BASE_URL}/api/auth/refresh-token`,
      JSON.stringify({ userId, refreshToken }),
      { headers },
    );
    refreshDuration.add(Date.now() - start);
    const ok = check(res, { 'refresh ok': (r) => r.status === 200 });
    errorRate.add(!ok);
    if (ok) {
      const body = res.json();
      // 轮换后旧 RT 失效；下一轮迭代会重新 login
      refreshToken = body.refreshToken || body.RefreshToken || refreshToken;
    }
  });

  sleep(1);
}
