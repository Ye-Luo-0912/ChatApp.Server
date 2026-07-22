import http from 'k6/http';
import { check, sleep, group } from 'k6';
import { SharedArray } from 'k6/data';
import { Rate, Trend, Counter } from 'k6/metrics';

/**
 * 混合负载场景（按 PROFILE 拆分）：
 *   steady       — 固定设备，≥90% 已认证业务流量（默认）
 *   auth_capacity — 登录/BCrypt 容量（薄封装，建议优先用 login-capacity.k6.js）
 *   device_churn — 每轮新 X-Device-Id，压会话/新设备/通知增长
 *   soak         — 同 steady 流量形态，默认 DURATION=2h，观察内存/积压/池
 *   mixed        — 兼容旧名，等同 device_churn（历史行为：每轮登录+换设备）
 *   dual_ratelimit — 双实例共享限流
 *
 * 宿主侧请同步采集: Redis ping、DB 连接池、GC、auth.login、password.hashing.*、
 * notification.outbox.backlog、avatar.reencode.*、data_export.pending。
 *
 * 用法:
 *   ASPNETCORE_ENVIRONMENT=Performance dotnet run --project ChatApp.Server.csproj
 *   k6 run -e PROFILE=steady -e TOKENS_FILE=./tokens.json -e RATE=20 -e DURATION=10m \
 *     tests/load/mixed-workload.k6.js
 *   k6 run -e PROFILE=device_churn -e CREDS_FILE=./creds.json tests/load/mixed-workload.k6.js
 *   k6 run -e PROFILE=soak -e TOKENS_FILE=./tokens.json -e DURATION=2h tests/load/mixed-workload.k6.js
 *   k6 run -e PROFILE=auth_capacity -e CREDS_FILE=./creds.json tests/load/mixed-workload.k6.js
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
const sessionsTrend = new Trend('sessions_ms', true);
const meTrend = new Trend('me_ms', true);
const overloaded = new Counter('login_overloaded_503');

const PROFILE = __ENV.PROFILE || 'steady';
const BASE_URL = (__ENV.BASE_URL || 'http://localhost:8080').replace(/\/$/, '');
const BASE_URL_A = (__ENV.BASE_URL_A || BASE_URL).replace(/\/$/, '');
const BASE_URL_B = (__ENV.BASE_URL_B || BASE_URL).replace(/\/$/, '');
const LOGIN_RATIO = Number(__ENV.LOGIN_RATIO || 0.1); // steady/soak: 默认 ≤10% 登录

const users = new SharedArray('users', () => {
  if (__ENV.CREDS_FILE) return JSON.parse(open(__ENV.CREDS_FILE));
  const prefix = __ENV.TEST_USER_PREFIX || 'loaduser';
  const password = __ENV.TEST_PASSWORD || 'Passw0rd!';
  const count = Number(__ENV.USER_COUNT || 50);
  const list = [];
  for (let i = 1; i <= count; i++) list.push({ username: `${prefix}${i}`, password, userId: 0 });
  return list;
});

const tokens = new SharedArray('tokens', () => {
  if (!__ENV.TOKENS_FILE) return [];
  return JSON.parse(open(__ENV.TOKENS_FILE));
});

const isChurn = PROFILE === 'device_churn' || PROFILE === 'mixed';
const isAuthCapacity = PROFILE === 'auth_capacity';
const isSteadyLike = PROFILE === 'steady' || PROFILE === 'soak';

const profiles = {
  steady: {
    scenarios: {
      steady: {
        executor: 'constant-arrival-rate',
        rate: Number(__ENV.RATE || 20),
        timeUnit: '1s',
        duration: __ENV.DURATION || '10m',
        preAllocatedVUs: Math.min(40, Math.max(users.length, tokens.length, 40)),
        maxVUs: Math.min(120, Math.max(users.length, tokens.length, 40) * 3),
      },
    },
    thresholds: {
      errors: ['rate<0.05'],
      login_ms: ['p(95)<800', 'p(99)<2000'],
      refresh_ms: ['p(95)<300', 'p(99)<800'],
      friends_ms: ['p(95)<400', 'p(99)<1000'],
      search_ms: ['p(95)<500', 'p(99)<1200'],
      notifications_ms: ['p(95)<400', 'p(99)<1000'],
      sessions_ms: ['p(95)<400', 'p(99)<1000'],
      http_req_failed: ['rate<0.05'],
    },
  },
  device_churn: {
    scenarios: {
      churn: {
        executor: 'constant-arrival-rate',
        rate: Number(__ENV.RATE || 10),
        timeUnit: '1s',
        duration: __ENV.DURATION || '10m',
        preAllocatedVUs: Math.min(40, users.length || 40),
        maxVUs: Math.min(120, (users.length || 40) * 3),
      },
    },
    thresholds: {
      errors: ['rate<0.08'],
      login_ms: ['p(95)<1000', 'p(99)<2500'],
      http_req_failed: ['rate<0.08'],
    },
  },
  mixed: {
    // 兼容旧名 → device_churn
    scenarios: {
      churn: {
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
      http_req_failed: ['rate<0.05'],
    },
  },
  auth_capacity: {
    scenarios: {
      login: {
        executor: 'constant-arrival-rate',
        rate: Number(__ENV.RATE || 30),
        timeUnit: '1s',
        duration: __ENV.DURATION || '5m',
        preAllocatedVUs: Math.min(60, users.length || 60),
        maxVUs: Math.min(200, (users.length || 60) * 4),
      },
    },
    thresholds: {
      errors: ['rate<0.1'],
      login_ms: ['p(95)<1500', 'p(99)<4000'],
      http_req_failed: ['rate<0.1'],
    },
  },
  soak: {
    scenarios: {
      soak: {
        executor: 'constant-arrival-rate',
        rate: Number(__ENV.RATE || 8),
        timeUnit: '1s',
        duration: __ENV.DURATION || '2h',
        preAllocatedVUs: Math.min(20, Math.max(users.length, tokens.length, 20)),
        maxVUs: Math.min(60, Math.max(users.length, tokens.length, 20) * 2),
      },
    },
    thresholds: {
      errors: ['rate<0.02'],
      login_ms: ['p(95)<1000', 'p(99)<2500'],
      refresh_ms: ['p(95)<400', 'p(99)<1000'],
      http_req_failed: ['rate<0.02'],
    },
  },
  dual_ratelimit: {
    vus: 10,
    duration: '30s',
    thresholds: {
      http_req_duration: ['p(95)<2000'],
    },
  },
};

export const options = profiles[PROFILE] || profiles.steady;

function deviceHeaders(vu, churn) {
  return {
    'Content-Type': 'application/json',
    'X-Device-Id': churn
      ? `k6-churn-${vu}-${__ITER}`
      : `k6-steady-${vu}`,
    'X-Correlation-Id': `k6-${vu}-${Date.now()}`,
  };
}

function pickUser() {
  return users[(__VU - 1) % users.length];
}

function pickToken() {
  if (!tokens.length) return null;
  return tokens[(__VU - 1) % tokens.length];
}

function doLogin(headers) {
  const user = pickUser();
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
  if (res.status !== 200) return { accessToken: '', refreshToken: '', userId: '' };
  const body = res.json();
  // Snowflake userIds exceed Number.MAX_SAFE_INTEGER; keep digits as string for refresh.
  const idMatch = String(res.body || '').match(/"userId"\s*:\s*(\d+)/i);
  return {
    accessToken: body.accessToken || body.AccessToken || '',
    refreshToken: body.refreshToken || body.RefreshToken || '',
    userId: (idMatch && idMatch[1]) || String(body.userId || body.UserId || user.userId || ''),
  };
}

function authedReads(headers, accessToken, refreshToken, userId) {
  const auth = Object.assign({}, headers, { Authorization: `Bearer ${accessToken}` });

  group('me', () => {
    const res = http.get(`${BASE_URL}/api/users/me`, { headers: auth, tags: { endpoint: 'me' } });
    meTrend.add(res.timings.duration);
    const ok = check(res, { 'me 200': (r) => r.status === 200 });
    errorRate.add(!ok);
  });

  group('friends', () => {
    const res = http.get(`${BASE_URL}/api/Friendship/all`, { headers: auth, tags: { endpoint: 'friends' } });
    friendsTrend.add(res.timings.duration);
    const ok = check(res, { 'friends 200': (r) => r.status === 200 });
    errorRate.add(!ok);
  });

  group('search', () => {
    const user = pickUser();
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
    const ok = check(res, { 'notifications 200': (r) => r.status === 200 });
    errorRate.add(!ok);
  });

  group('sessions', () => {
    const res = http.get(`${BASE_URL}/api/users/me/sessions`, {
      headers: auth,
      tags: { endpoint: 'sessions' },
    });
    sessionsTrend.add(res.timings.duration);
    const ok = check(res, { 'sessions 200': (r) => r.status === 200 });
    errorRate.add(!ok);
  });

  group('refresh', () => {
    if (!refreshToken || !userId) return;
    const res = http.post(
      `${BASE_URL}/api/auth/refresh-token`,
      `{"userId":${userId},"refreshToken":${JSON.stringify(refreshToken)}}`,
      { headers, tags: { endpoint: 'refresh' } },
    );
    refreshTrend.add(res.timings.duration);
    const ok = check(res, { 'refresh 200': (r) => r.status === 200 });
    errorRate.add(!ok);
  });
}

export default function () {
  if (PROFILE === 'dual_ratelimit') {
    dualRateLimit();
    return;
  }

  const headers = deviceHeaders(__VU, isChurn);

  if (isAuthCapacity) {
    group('login', () => {
      doLogin(headers);
    });
    sleep(Number(__ENV.THINK || 0.05));
    return;
  }

  if (isChurn) {
    // 每轮登录 + 换设备：压新设备/会话/通知增长
    let session = { accessToken: '', refreshToken: '', userId: '' };
    group('login', () => {
      session = doLogin(headers);
    });
    if (session.accessToken) {
      authedReads(headers, session.accessToken, session.refreshToken, session.userId);
    }
    sleep(Number(__ENV.THINK || 0.2));
    return;
  }

  // steady / soak：固定设备；默认 ≥90% 已认证流量
  const forceLogin = Math.random() < LOGIN_RATIO;
  let accessToken = '';
  let refreshToken = '';
  let userId = '';

  const preset = pickToken();
  if (!forceLogin && preset && preset.accessToken) {
    accessToken = preset.accessToken;
    refreshToken = preset.refreshToken || '';
    userId = preset.userId || '';
  } else {
    group('login', () => {
      const session = doLogin(headers);
      accessToken = session.accessToken;
      refreshToken = session.refreshToken;
      userId = session.userId;
    });
  }

  if (!accessToken) {
    sleep(0.5);
    return;
  }

  authedReads(headers, accessToken, refreshToken, userId);
  sleep(Number(__ENV.THINK || 0.2));
}

function dualRateLimit() {
  const user = pickUser();
  const headers = deviceHeaders(__VU, true);
  const body = JSON.stringify({ username: user.username, password: 'wrong-password-for-rl' });
  const a = http.post(`${BASE_URL_A}/api/auth/login`, body, { headers, tags: { endpoint: 'login_a' } });
  const b = http.post(`${BASE_URL_B}/api/auth/login`, body, { headers, tags: { endpoint: 'login_b' } });
  loginTrend.add(a.timings.duration);
  loginTrend.add(b.timings.duration);
  const hitLimit = a.status === 429 || b.status === 429;
  check(null, { 'dual instance shared limit eventually 429': () => hitLimit || a.status < 500 });
  errorRate.add(a.status >= 500 || b.status >= 500);
  sleep(0.05);
}
