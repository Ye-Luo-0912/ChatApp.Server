import http from 'k6/http';
import { check, sleep, group } from 'k6';
import { SharedArray } from 'k6/data';
import { Rate, Trend, Counter, Gauge } from 'k6/metrics';

/**
 * 混合负载场景（按 PROFILE 拆分）：
 *   steady       — 固定设备，≥90% 已认证业务流量（默认）
 *   auth_capacity — 登录/BCrypt 容量（薄封装，建议优先用 login-capacity.k6.js）
 *   device_churn — 每轮新 X-Installation-Id，压会话/新设备/通知增长
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
const searchTrend = new Trend('search_ms', true);
const notifyTrend = new Trend('notifications_ms', true);
const sessionsTrend = new Trend('sessions_ms', true);
const meTrend = new Trend('me_ms', true);
// Response header is emitted only in Performance/Testing. This is the
// endpoint-scoped assertion that a warmed authenticated read stays off DB.
const meDbQueriesTrend = new Trend('me_db_queries');
const meAuthDbQueriesTrend = new Trend('me_auth_db_queries');
const overloaded = new Counter('login_overloaded_503');

// 宿主侧 delta 指标：teardown() 中从 /debug/metrics 采样并计算差值。
// compare-baseline.mjs 据此 + http_reqs 计算 per-request 指标。
const allocationsDelta = new Gauge('allocations_delta_bytes');
const redisCmdsDelta = new Gauge('redis_cmds_delta');
const dbQueriesDelta = new Gauge('db_queries_delta');

function responseHeader(headers, name) {
  // net/http canonicalizes response headers (for example, Db-Commands),
  // while k6 preserves the received key. Keep this lookup allocation-free
  // on the request hot path and accept the common casing variants.
  if (name === 'X-ChatApp-Auth-Db-Commands') {
    return headers[name]
      ?? headers['X-Chatapp-Auth-Db-Commands']
      ?? headers['x-chatapp-auth-db-commands'];
  }

  return headers[name]
    ?? headers['X-Chatapp-Db-Commands']
    ?? headers['x-chatapp-db-commands'];
}

const PROFILE = __ENV.PROFILE || 'steady';
const BASE_URL = (__ENV.BASE_URL || 'http://localhost:8080').replace(/\/$/, '');
const BASE_URL_A = (__ENV.BASE_URL_A || BASE_URL).replace(/\/$/, '');
const BASE_URL_B = (__ENV.BASE_URL_B || BASE_URL).replace(/\/$/, '');
const LOGIN_RATIO = Number(__ENV.LOGIN_RATIO || 0.1); // steady/soak: 默认 ≤10% 登录
// 注意：真实客户端（Chat_App）从不主动随机刷新——refresh 仅在「启动自动登录」与
// 「请求遇 401 时刷新一次并重放」两个时机发生（LoginViewModel / AuthInterceptor）。
// steady/soak 已内置这两个语义，不在此随机触发刷新。

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

// VU 级会话状态：跨迭代保持令牌，refresh 成功后写回新令牌，避免无效刷新风暴。
// k6 中每个 VU 有独立 JS 上下文，全局对象天然按 VU 隔离。
const vuSessions = {};

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
      // /api/users/me itself performs one profile projection query. A warmed
      // auth path must not add another query, so two queries at p95 is a
      // regression (one endpoint query + one authentication-fence query).
      me_db_queries: ['p(95)<2'],
      // The /me endpoint may legitimately execute its profile projection,
      // but a warmed authenticated request must never execute the auth fence
      // projection itself.
      // Counts are integers; <0.5 is the portable k6 spelling of p95 == 0.
      me_auth_db_queries: ['p(95)<0.5'],
      login_ms: ['p(95)<800', 'p(99)<2000'],
      refresh_ms: ['p(95)<300', 'p(99)<800'],
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

function buildInstallationId(vu, churn) {
  const vuPart = String(vu).padStart(6, '0');
  const iterPart = String(__ITER).padStart(10, '0');
  return churn
    ? `k6-churn-installation-${vuPart}-${iterPart}`
    : `k6-steady-installation-${vuPart}`;
}

function deviceHeaders(vu, churn, deviceCredential = '', installationId = '') {
  const headers = {
    'Content-Type': 'application/json',
    'X-Installation-Id': installationId || buildInstallationId(vu, churn),
    'X-Correlation-Id': `k6-${vu}-${Date.now()}`,
  };
  if (deviceCredential) headers['X-Device-Credential'] = deviceCredential;
  return headers;
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
  if (res.status !== 200) {
    return { accessToken: '', refreshToken: '', deviceCredential: '', userId: '', installationId: headers['X-Installation-Id'] || '' };
  }
  const body = res.json();
  // Snowflake userIds exceed Number.MAX_SAFE_INTEGER; keep digits as string for refresh.
  const idMatch = String(res.body || '').match(/"userId"\s*:\s*(\d+)/i);
  return {
    accessToken: body.accessToken || body.AccessToken || '',
    refreshToken: body.refreshToken || body.RefreshToken || '',
    deviceCredential: body.deviceCredential || body.DeviceCredential || '',
    userId: (idMatch && idMatch[1]) || String(body.userId || body.UserId || user.userId || ''),
    installationId: headers['X-Installation-Id'] || '',
  };
}

// 刷新一次；成功则更新会话令牌，失败则清空 accessToken（会话失效语义）。
function refreshOnce(session) {
  if (!session.refreshToken || !session.userId) {
    session.accessToken = '';
    return session;
  }
  group('refresh', () => {
    const res = http.post(
      `${BASE_URL}/api/auth/refresh-token`,
      `{"userId":${session.userId},"refreshToken":${JSON.stringify(session.refreshToken)}}`,
      { headers: deviceHeaders(__VU, false, session.deviceCredential, session.installationId), tags: { endpoint: 'refresh' } },
    );
    refreshTrend.add(res.timings.duration);
    const ok = check(res, { 'refresh 200': (r) => r.status === 200 });
    errorRate.add(!ok);
    if (res.status === 200) {
      try {
        const body = res.json();
        if (body.accessToken) session.accessToken = body.accessToken;
        if (body.refreshToken) session.refreshToken = body.refreshToken;
        if (body.deviceCredential) session.deviceCredential = body.deviceCredential;
      } catch (e) {
        session.accessToken = '';
      }
    } else {
      // 刷新失败 → 会话失效（对齐 TokenInfo：清令牌并回登录页）
      session.accessToken = '';
    }
  });
  return session;
}

// 执行已认证读取。令牌生命周期语义对齐真实客户端 (AuthInterceptor + TokenInfo)：
//   请求遇 401/403 → 刷新一次 → 用新令牌重放原请求；
//   刷新失败 → 会话失效，本迭代后续请求不再发出，下次迭代回登录页重新登录。
function authedReads(headers, accessToken, refreshToken, deviceCredential, userId, installationId) {
  const session = { accessToken, refreshToken, deviceCredential, userId, installationId };
  const authFor = (t) => Object.assign({}, headers, { Authorization: `Bearer ${t}` });
  let auth = authFor(session.accessToken);

  group('me', () => {
    let res = http.get(`${BASE_URL}/api/users/me`, { headers: auth, tags: { endpoint: 'me' } });
    meTrend.add(res.timings.duration);
    const dbCommands = Number(responseHeader(res.headers, 'X-ChatApp-Db-Commands') ?? NaN);
    if (Number.isFinite(dbCommands)) meDbQueriesTrend.add(dbCommands);
    const authDbCommands = Number(
      responseHeader(res.headers, 'X-ChatApp-Auth-Db-Commands') ?? NaN);
    if (Number.isFinite(authDbCommands)) meAuthDbQueriesTrend.add(authDbCommands);
    // 401 → 刷新一次 → 用新令牌重放（对齐 AuthInterceptor）
    if (res.status === 401 || res.status === 403) {
      refreshOnce(session);
      if (session.accessToken) {
        auth = authFor(session.accessToken);
        res = http.get(`${BASE_URL}/api/users/me`, { headers: auth, tags: { endpoint: 'me' } });
        meTrend.add(res.timings.duration);
        const db2 = Number(responseHeader(res.headers, 'X-ChatApp-Db-Commands') ?? NaN);
        if (Number.isFinite(db2)) meDbQueriesTrend.add(db2);
        const adb2 = Number(
          responseHeader(res.headers, 'X-ChatApp-Auth-Db-Commands') ?? NaN);
        if (Number.isFinite(adb2)) meAuthDbQueriesTrend.add(adb2);
      }
    }
    const ok = check(res, { 'me 200': (r) => r.status === 200 });
    errorRate.add(!ok);
  });

  // search / notifications / sessions：使用最新令牌；401 时刷新并重放一次，
  // 刷新失败则跳过后续请求（会话已失效，对齐客户端行为，不产生垃圾 401）。
  const authedGet = (url, tag, trend) => {
    if (!session.accessToken) return;
    let res = http.get(url, { headers: auth, tags: { endpoint: tag } });
    trend.add(res.timings.duration);
    if (res.status === 401 || res.status === 403) {
      refreshOnce(session);
      if (!session.accessToken) return;
      auth = authFor(session.accessToken);
      res = http.get(url, { headers: auth, tags: { endpoint: tag } });
      trend.add(res.timings.duration);
    }
    const ok = check(res, { [`${tag} 200`]: (r) => r.status === 200 });
    errorRate.add(!ok);
  };

  group('search', () => {
    const user = pickUser();
    const q = encodeURIComponent((user.username || 'a').slice(0, 3));
    authedGet(`${BASE_URL}/api/users/search?q=${q}&limit=10`, 'search', searchTrend);
  });

  group('notifications', () => {
    authedGet(`${BASE_URL}/api/users/me/notifications?limit=20`, 'notifications', notifyTrend);
  });

  group('sessions', () => {
    authedGet(`${BASE_URL}/api/users/me/sessions`, 'sessions', sessionsTrend);
  });

  return session;
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
    let session = { accessToken: '', refreshToken: '', deviceCredential: '', userId: '', installationId: headers['X-Installation-Id'] || '' };
    group('login', () => {
      session = doLogin(headers);
    });
    if (session.accessToken) {
      authedReads(headers, session.accessToken, session.refreshToken, session.deviceCredential, session.userId, session.installationId);
    }
    sleep(Number(__ENV.THINK || 0.2));
    return;
  }

  // steady / soak：VU 级会话跨迭代保持。会话恢复语义对齐真实客户端：
  //   - 启动自动登录：用本地令牌先刷新一次（对应 LoginViewModel 启动时 RefreshTokensAsync）
  //   - 401 → 刷新一次并重放（对应 AuthInterceptor）；刷新失败 → 回登录页重新登录
  //   - 无每迭代随机刷新（真实客户端从不主动刷新）
  const forceLogin = Math.random() < LOGIN_RATIO;

  // 首次迭代或会话丢失时初始化
  if (!vuSessions[__VU] || !vuSessions[__VU].accessToken) {
    let session = null;
    if (!forceLogin) {
      const preset = pickToken();
      if (preset && preset.accessToken) {
        const installationId = String(preset.deviceId || preset.installationId || preset.device || '');
        if (!installationId) {
          throw new Error('TOKENS_FILE entry is missing deviceId/installationId; preset tokens must reuse the login installation ID');
        }
        session = {
          accessToken: preset.accessToken,
          refreshToken: preset.refreshToken || '',
          deviceCredential: preset.deviceCredential || preset.DeviceCredential || '',
          userId: String(preset.userId || ''),
          installationId,
        };
        // 启动自动登录：先用本地令牌刷新一次；失败则本迭代回登录页
        refreshOnce(session);
        if (!session.accessToken) session = null;
      }
    }
    // 无预设令牌、刷新失败或强制登录时走 login（回登录页重新登录）
    if (!session || !session.accessToken) {
      group('login', () => {
        session = doLogin(headers);
      });
      if (!session.accessToken) session = null;
    }
    if (session) vuSessions[__VU] = session;
  }

  if (!vuSessions[__VU] || !vuSessions[__VU].accessToken) {
    sleep(0.5);
    return;
  }

  const s = vuSessions[__VU];
  const sessionHeaders = deviceHeaders(__VU, false, s.deviceCredential, s.installationId);
  const updated = authedReads(sessionHeaders, s.accessToken, s.refreshToken, s.deviceCredential, s.userId, s.installationId);

  // 写回更新后的令牌（refresh 成功时为新令牌，会话失效时为空触发重新登录）
  vuSessions[__VU] = updated;

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

// ─────────────────────────────────────────────────────────────
// 宿主侧指标采样：setup() 记录基线，teardown() 计算差值并上报。
// /debug/metrics 端点需在 API 侧启用（见 Program.cs）。
// ─────────────────────────────────────────────────────────────
export function setup() {
  if (PROFILE === 'dual_ratelimit') return null;
  const res = http.get(`${BASE_URL}/debug/metrics`, { tags: { endpoint: 'host_metrics_setup' } });
  if (res.status !== 200)
    throw new Error(`host metrics endpoint unavailable during setup: HTTP ${res.status}`);

  let data;
  try { data = JSON.parse(res.body); } catch (e) {
    throw new Error(`host metrics setup response is not JSON: ${e}`);
  }
  for (const key of ['allocated_bytes', 'redis_total_commands', 'db_total_commands']) {
    if (!Number.isFinite(Number(data[key])))
      throw new Error(`host metrics setup missing numeric field: ${key}`);
  }
  return data;
}

export function teardown(data) {
  if (!data) return;
  const res = http.get(`${BASE_URL}/debug/metrics`, { tags: { endpoint: 'host_metrics_teardown' } });
  if (res.status !== 200)
    throw new Error(`host metrics endpoint unavailable during teardown: HTTP ${res.status}`);

  let end;
  try { end = JSON.parse(res.body); } catch (e) {
    throw new Error(`host metrics teardown response is not JSON: ${e}`);
  }
  for (const key of ['allocated_bytes', 'redis_total_commands', 'db_total_commands']) {
    if (!Number.isFinite(Number(end[key])))
      throw new Error(`host metrics teardown missing numeric field: ${key}`);
  }
  allocationsDelta.add(Math.max(0, end.allocated_bytes - data.allocated_bytes));
  redisCmdsDelta.add(Math.max(0, end.redis_total_commands - data.redis_total_commands));
  dbQueriesDelta.add(Math.max(0, end.db_total_commands - data.db_total_commands));
}
