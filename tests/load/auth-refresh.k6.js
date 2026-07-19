import http from 'k6/http';
import { check, sleep, group } from 'k6';
import { Rate, Trend } from 'k6/metrics';

/**
 * 认证与好友路径压测
 *
 * 用法示例：
 *   k6 run -e BASE_URL=https://localhost:5001 -e TEST_USER=demo -e TEST_PASSWORD=secret tests/load/auth-refresh.k6.js
 *   k6 run -e PROFILE=soak tests/load/auth-refresh.k6.js      # 30 分钟恒定到达率
 *   k6 run -e PROFILE=spike tests/load/auth-refresh.k6.js     # 峰值冲击
 */

const errorRate = new Rate('errors');
const loginDuration = new Trend('login_duration', true);
const refreshDuration = new Trend('refresh_duration', true);
const friendsDuration = new Trend('friends_duration', true);

const PROFILE = __ENV.PROFILE || 'smoke';

const profiles = {
  smoke: {
    vus: 5,
    duration: '1m',
  },
  soak: {
    scenarios: {
      steady: {
        executor: 'constant-arrival-rate',
        rate: 40,
        timeUnit: '1s',
        duration: '30m',
        preAllocatedVUs: 40,
        maxVUs: 80,
      },
    },
  },
  spike: {
    stages: [
      { duration: '30s', target: 10 },
      { duration: '1m', target: 100 },
      { duration: '2m', target: 100 },
      { duration: '30s', target: 10 },
    ],
  },
};

export const options = {
  ...profiles[PROFILE],
  thresholds: {
    http_req_failed: ['rate<0.001'],
    http_req_duration: ['p(95)<100', 'p(99)<250'],
    errors: ['rate<0.001'],
    login_duration: ['p(95)<500'], // BCrypt 单独放宽
    refresh_duration: ['p(95)<100', 'p(99)<250'],
    friends_duration: ['p(95)<100', 'p(99)<250'],
  },
};

const BASE_URL = __ENV.BASE_URL || 'https://localhost:5001';
const headers = { 'Content-Type': 'application/json', 'X-Device-Id': 'k6-load-device-00123456' };

export function setup() {
  const res = http.post(
    `${BASE_URL}/api/auth/login`,
    JSON.stringify({
      username: __ENV.TEST_USER || 'demo',
      password: __ENV.TEST_PASSWORD || 'password',
    }),
    { headers },
  );
  check(res, { 'setup login 200': (r) => r.status === 200 });
  const body = res.json();
  return {
    accessToken: body.accessToken || body.AccessToken,
    refreshToken: body.refreshToken || body.RefreshToken,
    userId: body.userId || body.UserId,
  };
}

export default function (data) {
  group('login', () => {
    const start = Date.now();
    const res = http.post(
      `${BASE_URL}/api/auth/login`,
      JSON.stringify({
        username: __ENV.TEST_USER || 'demo',
        password: __ENV.TEST_PASSWORD || 'password',
      }),
      { headers },
    );
    loginDuration.add(Date.now() - start);
    const ok = check(res, { 'login ok': (r) => r.status === 200 });
    errorRate.add(!ok);
  });

  group('authenticated friends page', () => {
    const authHeaders = {
      ...headers,
      Authorization: `Bearer ${data.accessToken}`,
    };
    const start = Date.now();
    const res = http.get(`${BASE_URL}/api/Friendship/all?limit=50`, { headers: authHeaders });
    friendsDuration.add(Date.now() - start);
    const ok = check(res, { 'friends page ok': (r) => r.status === 200 });
    errorRate.add(!ok);

    const search = http.get(`${BASE_URL}/api/Friendship/search?searchTerm=te&limit=20`, {
      headers: authHeaders,
    });
    check(search, { 'search ok or empty': (r) => r.status === 200 || r.status === 400 });
  });

  group('refresh', () => {
    const start = Date.now();
    const res = http.post(
      `${BASE_URL}/api/auth/refresh-token`,
      JSON.stringify({
        userId: data.userId,
        refreshToken: data.refreshToken,
      }),
      { headers },
    );
    refreshDuration.add(Date.now() - start);
    // 并发刷新同一令牌时仅一次成功；压测中允许 400，但不应 5xx
    const ok = check(res, { 'refresh not 5xx': (r) => r.status < 500 });
    errorRate.add(!ok);
    if (res.status === 200) {
      const body = res.json();
      data.accessToken = body.accessToken || body.AccessToken || data.accessToken;
      data.refreshToken = body.refreshToken || body.RefreshToken || data.refreshToken;
    }
  });

  sleep(0.2);
}
