import http from 'k6/http';
import { check, sleep } from 'k6';

/**
 * 压测骨架：替换 BASE_URL / refresh 凭据后运行
 *   k6 run tests/load/auth-refresh.k6.js
 */
export const options = {
  vus: 20,
  duration: '2m',
  thresholds: {
    http_req_failed: ['rate<0.001'],
    http_req_duration: ['p(95)<100', 'p(99)<250'],
  },
};

const BASE_URL = __ENV.BASE_URL || 'https://localhost:5001';

export default function () {
  const loginRes = http.post(`${BASE_URL}/api/auth/login`, JSON.stringify({
    username: __ENV.TEST_USER || 'demo',
    password: __ENV.TEST_PASSWORD || 'password',
  }), { headers: { 'Content-Type': 'application/json' } });

  check(loginRes, { 'login ok': (r) => r.status === 200 });
  sleep(1);
}
