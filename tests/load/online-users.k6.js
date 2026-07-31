import http from 'k6/http';
import { check, sleep, group } from 'k6';
import { SharedArray } from 'k6/data';
import { Rate, Trend } from 'k6/metrics';

/**
 * 在线用户场景：setup 登录一次，迭代内只打业务读 + 刷新。
 * 注意：setup 同样受登录限流影响，可能拿不满会话；Performance 环境请调高限流或注入预置令牌。
 *
 *   k6 run -e BASE_URL=http://localhost:8080 -e CREDS_FILE=./creds.json -e PROFILE=smoke tests/load/online-users.k6.js
 */

const errorRate = new Rate('errors');
const meDuration = new Trend('me_duration', true);
const friendsDuration = new Trend('friends_duration', true);
const searchDuration = new Trend('search_duration', true);
const userSearchDuration = new Trend('user_search_duration', true);
const refreshDuration = new Trend('refresh_duration', true);

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
    userId: 0,
  }));
});

const profiles = {
  smoke: { vus: Math.min(10, users.length || 10), duration: '2m' },
  soak: {
    scenarios: {
      online: {
        executor: 'constant-vus',
        vus: Math.min(40, users.length || 40),
        duration: '30m',
      },
    },
  },
};

export const options = {
  ...profiles[PROFILE],
  thresholds: {
    http_req_failed: ['rate<0.01'],
    me_duration: ['p(95)<100'],
    friends_duration: ['p(95)<100'],
    errors: ['rate<0.01'],
  },
};

export function setup() {
  if (__ENV.TOKENS_FILE) {
    const sessions = JSON.parse(open(__ENV.TOKENS_FILE));
    return {
      sessions: sessions.map((s) => ({
        ...s,
        deviceCredential: s.deviceCredential || s.DeviceCredential || '',
      })),
    };
  }

  const sessions = [];
  const limit = Math.min(users.length, Number(__ENV.SETUP_LOGIN_LIMIT || users.length));
  for (let i = 0; i < limit; i++) {
    const u = users[i];
    const res = http.post(
      `${BASE_URL}/api/auth/login`,
      JSON.stringify({ username: u.username, password: u.password }),
      {
        headers: {
          'Content-Type': 'application/json',
          'X-Installation-Id': `k6-online-${i + 1}`,
        },
      },
    );
    if (res.status !== 200) continue;
    const body = res.json();
    sessions.push({
      accessToken: body.accessToken || body.AccessToken,
      refreshToken: body.refreshToken || body.RefreshToken,
      deviceCredential: body.deviceCredential || body.DeviceCredential || '',
      userId: body.userId || body.UserId || u.userId,
      device: `k6-online-${i + 1}`,
    });
  }
  if (sessions.length === 0) {
    throw new Error('setup: 无可用会话。请使用 Performance 环境或 -e TOKENS_FILE=./tokens.json');
  }
  return { sessions };
}

export default function (data) {
  const session = data.sessions[(__VU - 1) % data.sessions.length];
  const headers = {
    Authorization: `Bearer ${session.accessToken}`,
    'X-Installation-Id': session.device,
    'X-Correlation-Id': `online-${__VU}-${__ITER}`,
  };

  group('me', () => {
    const start = Date.now();
    const res = http.get(`${BASE_URL}/api/users/me`, { headers });
    meDuration.add(Date.now() - start);
    errorRate.add(!check(res, { me: (r) => r.status === 200 }));
  });

  group('friends', () => {
    const start = Date.now();
    const res = http.get(`${BASE_URL}/api/Friendship/all?limit=50`, { headers });
    friendsDuration.add(Date.now() - start);
    errorRate.add(!check(res, { friends: (r) => r.status === 200 }));
  });

  group('friend search', () => {
    const start = Date.now();
    const res = http.get(`${BASE_URL}/api/Friendship/search?searchTerm=a&limit=20`, { headers });
    searchDuration.add(Date.now() - start);
    check(res, { search: (r) => r.status === 200 || r.status === 400 });
  });

  group('user search', () => {
    const start = Date.now();
    const res = http.get(`${BASE_URL}/api/users/search?q=lo&limit=20`, { headers });
    userSearchDuration.add(Date.now() - start);
    check(res, { userSearch: (r) => r.status === 200 });
  });

  if (__ITER % 10 === 0) {
    group('refresh', () => {
      const start = Date.now();
      const res = http.post(
        `${BASE_URL}/api/auth/refresh-token`,
        JSON.stringify({ userId: session.userId, refreshToken: session.refreshToken }),
        { headers: {
          'Content-Type': 'application/json',
          'X-Installation-Id': session.device,
          ...(session.deviceCredential ? { 'X-Device-Credential': session.deviceCredential } : {}),
        } },
      );
      refreshDuration.add(Date.now() - start);
      if (res.status === 200) {
        const body = res.json();
        session.accessToken = body.accessToken || body.AccessToken || session.accessToken;
        session.refreshToken = body.refreshToken || body.RefreshToken || session.refreshToken;
        session.deviceCredential = body.deviceCredential || body.DeviceCredential || session.deviceCredential;
      }
    });
  }

  sleep(1);
}
