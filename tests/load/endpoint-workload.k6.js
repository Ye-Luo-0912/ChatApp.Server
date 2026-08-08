import http from 'k6/http';
import { check, sleep } from 'k6';
import { SharedArray } from 'k6/data';
import { Counter, Gauge, Trend } from 'k6/metrics';

/*
 * One endpoint per process.  Keeping the scenario isolated is intentional:
 * host deltas (DB, Garnet, allocations, pool wait and GC) can then be
 * attributed to one route instead of being hidden by the mixed workload.
 *
 * Examples:
 *   k6 run -e SCENARIO=auth-read -e TOKENS_FILE=./tokens.json endpoint-workload.k6.js
 *   k6 run -e SCENARIO=friends-read -e TOKENS_FILE=./tokens.json endpoint-workload.k6.js
 *   k6 run -e SCENARIO=presence -e PRESENCE_URL=http://... endpoint-workload.k6.js
 */

const SCENARIO = (__ENV.SCENARIO || 'auth-read').toLowerCase();
const BASE_URL = (__ENV.BASE_URL || 'http://127.0.0.1:5088').replace(/\/$/, '');
const PRESENCE_URL = __ENV.PRESENCE_URL || '';
const allowedScenarios = new Set([
  'auth-read',
  'friends-read',
  'search',
  'notifications',
  'sessions',
  'refresh',
  'presence',
  'attachment-ticket',
]);

if (!allowedScenarios.has(SCENARIO))
  throw new Error(`unsupported SCENARIO=${SCENARIO}`);
if (SCENARIO === 'presence' && !PRESENCE_URL)
  throw new Error('SCENARIO=presence requires PRESENCE_URL; Presence is a Realtime request/reply contract, not a Server HTTP route');

// k6 metric names may not contain a dash; keep the original scenario in
// route tags while using a stable metric-safe spelling in the summary.
const METRIC_SCENARIO = SCENARIO.replace(/[^A-Za-z0-9_]/g, '_');

const users = new SharedArray('endpoint-users', () => {
  if (__ENV.CREDS_FILE) return JSON.parse(open(__ENV.CREDS_FILE));
  const prefix = __ENV.TEST_USER_PREFIX || 'loaduser';
  const password = __ENV.TEST_PASSWORD || 'Passw0rd!';
  const count = Number(__ENV.USER_COUNT || 50);
  return Array.from({ length: count }, (_, index) => ({
    username: `${prefix}${index + 1}`,
    password,
  }));
});

const tokens = new SharedArray('endpoint-tokens', () =>
  __ENV.TOKENS_FILE ? JSON.parse(open(__ENV.TOKENS_FILE)) : []);

const latency = new Trend(`${METRIC_SCENARIO}_ms`, true);
const dbCommands = new Trend(`${METRIC_SCENARIO}_db_commands`);
const authDbCommands = new Trend(`${METRIC_SCENARIO}_auth_db_commands`);
const poolWait = new Trend(`${METRIC_SCENARIO}_pool_wait_ms`);
const errors = new Counter(`${METRIC_SCENARIO}_errors`);
const allocationsDelta = new Gauge(`${METRIC_SCENARIO}_allocations_delta_bytes`);
const garnetDelta = new Gauge(`${METRIC_SCENARIO}_garnet_commands_delta`);
const dbDelta = new Gauge(`${METRIC_SCENARIO}_db_commands_delta`);
const poolWaitDelta = new Gauge(`${METRIC_SCENARIO}_pool_wait_ms_delta`);
const gcPauseDelta = new Gauge(`${METRIC_SCENARIO}_gc_pause_ms_delta`);
const authGarnetDelta = new Gauge(`${METRIC_SCENARIO}_auth_garnet_reads_delta`);

const sessions = {};

export const options = {
  scenarios: {
    endpoint: {
      executor: 'constant-arrival-rate',
      rate: Number(__ENV.RATE || 20),
      timeUnit: '1s',
      duration: __ENV.DURATION || '3m',
      preAllocatedVUs: Math.min(40, Math.max(tokens.length, users.length, 10)),
      maxVUs: Math.min(120, Math.max(tokens.length, users.length, 10) * 3),
    },
  },
  thresholds: {
    [`${METRIC_SCENARIO}_errors`]: ['rate<0.01'],
    [`${METRIC_SCENARIO}_ms`]: ['p(95)<1000', 'p(99)<2500'],
  },
};

function header(headers, name) {
  if (name === 'X-ChatApp-Db-Commands') {
    return headers[name]
      ?? headers['X-Chatapp-Db-Commands']
      ?? headers['x-chatapp-db-commands'];
  }
  if (name === 'X-ChatApp-Auth-Db-Commands') {
    return headers[name]
      ?? headers['X-Chatapp-Auth-Db-Commands']
      ?? headers['x-chatapp-auth-db-commands'];
  }
  if (name === 'X-ChatApp-Db-Pool-Wait-Ms') {
    return headers[name]
      ?? headers['X-Chatapp-Db-Pool-Wait-Ms']
      ?? headers['x-chatapp-db-pool-wait-ms'];
  }
  return headers[name]
    ?? headers[name.toLowerCase()];
}

function numberHeader(headers, name) {
  const value = Number(header(headers, name));
  return Number.isFinite(value) ? value : null;
}

function installationId() {
  return `k6-endpoint-installation-${String(__VU).padStart(6, '0')}`;
}

function commonHeaders() {
  return {
    'Content-Type': 'application/json',
    'X-Installation-Id': installationId(),
    'X-Correlation-Id': `k6-endpoint-${SCENARIO}-${__VU}-${__ITER}`,
  };
}

function loginSession() {
  const user = users[(__VU - 1) % users.length];
  const request = http.post(
    `${BASE_URL}/api/auth/login`,
    JSON.stringify({ username: user.username, password: user.password }),
    { headers: commonHeaders(), tags: { route: 'setup-login', scenario: SCENARIO } },
  );
  if (request.status !== 200) return null;
  const body = request.json();
  const userId = String(request.body || '').match(/"userId"\s*:\s*(\d+)/i)?.[1]
    || String(body.userId || '');
  return {
    accessToken: body.accessToken || '',
    refreshToken: body.refreshToken || '',
    deviceCredential: body.deviceCredential || '',
    userId,
  };
}

function sessionForVu() {
  if (sessions[__VU]?.accessToken) return sessions[__VU];

  const preset = tokens.length ? tokens[(__VU - 1) % tokens.length] : null;
  if (preset?.accessToken) {
    sessions[__VU] = {
      accessToken: preset.accessToken,
      refreshToken: preset.refreshToken || '',
      deviceCredential: preset.deviceCredential || '',
      userId: String(preset.userId || ''),
    };
  } else {
    sessions[__VU] = loginSession();
  }
  return sessions[__VU];
}

function requestForScenario(session) {
  const headers = commonHeaders();
  headers.Authorization = `Bearer ${session.accessToken}`;
  const tags = { route: SCENARIO, scenario: SCENARIO };

  switch (SCENARIO) {
    case 'auth-read':
      return http.get(`${BASE_URL}/api/users/me`, { headers, tags });
    case 'friends-read':
      return http.get(`${BASE_URL}/api/friendship/all?limit=20`, { headers, tags });
    case 'search':
      return http.get(`${BASE_URL}/api/users/search?q=${encodeURIComponent(__ENV.SEARCH_Q || 'load')}&limit=10`, { headers, tags });
    case 'notifications':
      return http.get(`${BASE_URL}/api/users/me/notifications?limit=20`, { headers, tags });
    case 'sessions':
      return http.get(`${BASE_URL}/api/users/me/sessions`, { headers, tags });
    case 'refresh':
      headers['X-Device-Credential'] = session.deviceCredential;
      return http.post(
        `${BASE_URL}/api/auth/refresh-token`,
        `{"userId":${session.userId},"refreshToken":${JSON.stringify(session.refreshToken)}}`,
        { headers, tags },
      );
    case 'presence':
      return http.request(
        __ENV.PRESENCE_METHOD || 'POST',
        PRESENCE_URL,
        __ENV.PRESENCE_BODY || '{}',
        { headers, tags },
      );
    case 'attachment-ticket':
      return http.post(
        `${BASE_URL}/api/attachments/presign`,
        JSON.stringify({
          contentType: __ENV.ATTACHMENT_CONTENT_TYPE || 'application/octet-stream',
          contentLength: Number(__ENV.ATTACHMENT_BYTES || 1024),
          originalName: 'k6-performance.bin',
        }),
        { headers, tags },
      );
    default:
      throw new Error(`unsupported SCENARIO=${SCENARIO}`);
  }
}

function recordResponse(response) {
  latency.add(response.timings.duration, { route: SCENARIO });

  const db = numberHeader(response.headers, 'X-ChatApp-Db-Commands');
  if (db !== null) dbCommands.add(db, { route: SCENARIO });
  const authDb = numberHeader(response.headers, 'X-ChatApp-Auth-Db-Commands');
  if (authDb !== null) authDbCommands.add(authDb, { route: SCENARIO });
  const wait = numberHeader(response.headers, 'X-ChatApp-Db-Pool-Wait-Ms');
  if (wait !== null) poolWait.add(wait, { route: SCENARIO });

  const ok = check(response, {
    [`${METRIC_SCENARIO} expected response`]: (r) => r.status >= 200 && r.status < 300,
  });
  errors.add(!ok || response.status >= 500, { route: SCENARIO });

  if (response.status === 401 || response.status === 403)
    sessions[__VU] = null;

  if (SCENARIO === 'refresh' && response.status === 200) {
    const body = response.json();
    const current = sessions[__VU];
    sessions[__VU] = {
      ...current,
      accessToken: body.accessToken || current.accessToken,
      refreshToken: body.refreshToken || current.refreshToken,
      deviceCredential: body.deviceCredential || current.deviceCredential,
    };
  }
}

export default function () {
  const session = sessionForVu();
  if (!session?.accessToken) {
    errors.add(1, { route: SCENARIO });
    sleep(0.1);
    return;
  }
  recordResponse(requestForScenario(session));
  sleep(Number(__ENV.THINK || 0.05));
}

function metric(data, name) {
  const value = Number(data?.[name]);
  return Number.isFinite(value) ? value : null;
}

export function setup() {
  const response = http.get(`${BASE_URL}/debug/metrics`, {
    tags: { route: 'host-metrics-setup', scenario: SCENARIO },
  });
  if (response.status !== 200)
    throw new Error(`host metrics unavailable: HTTP ${response.status}`);
  const data = response.json();
  for (const key of ['allocated_bytes', 'redis_total_commands', 'db_total_commands', 'gc_pause_ms_total']) {
    if (metric(data, key) === null) throw new Error(`host metrics missing ${key}`);
  }
  return data;
}

export function teardown(start) {
  const response = http.get(`${BASE_URL}/debug/metrics`, {
    tags: { route: 'host-metrics-teardown', scenario: SCENARIO },
  });
  if (response.status !== 200)
    throw new Error(`host metrics unavailable: HTTP ${response.status}`);
  const end = response.json();

  function positiveDelta(name) {
    const before = metric(start, name);
    const after = metric(end, name);
    return before === null || after === null ? 0 : Math.max(0, after - before);
  }

  allocationsDelta.add(positiveDelta('allocated_bytes'), { route: SCENARIO });
  garnetDelta.add(positiveDelta('redis_total_commands'), { route: SCENARIO });
  dbDelta.add(positiveDelta('db_total_commands'), { route: SCENARIO });
  gcPauseDelta.add(positiveDelta('gc_pause_ms_total'), { route: SCENARIO });
  authGarnetDelta.add(positiveDelta('auth_fence_garnet_reads_total'), { route: SCENARIO });
  poolWaitDelta.add(positiveDelta('db_pool_wait_ms_total'), { route: SCENARIO });
}
