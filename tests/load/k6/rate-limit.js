import http from 'k6/http';
import { Counter, Trend } from 'k6/metrics';

/**
 * Stage 7 — 限流驗證。
 *
 * 計畫 §12 指定的兩個情境：
 *
 *   正常   10 req/s          → 應該全部通過
 *   異常   1000 req/s 同一人 → 大部分應該被 429 擋下
 *
 * 用 constant-arrival-rate（固定到達率）而不是固定 VU 數：
 * 我們要控制的是「每秒送出幾個請求」，而不是「幾個人在等」。
 * 用固定 VU 的話，被限流拒絕會讓請求變快、反而送出更多請求，
 * 量到的速率就不是我們設定的那個。
 */

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5080';
const PRODUCT_ID = __ENV.PRODUCT_ID;
const RATE = Number(__ENV.RATE || 10);
const DURATION = __ENV.DURATION || '10s';
const SUMMARY_FILE = __ENV.SUMMARY_FILE || 'summary.json';

// 'shared' = 所有請求共用同一個 X-User-Id（模擬單一使用者洗版）
// 'unique' = 每個 VU 各自的 Id（模擬正常的多人流量）
const USER_MODE = __ENV.USER_MODE || 'shared';
const SHARED_USER_ID = __ENV.USER_ID || '9999';

export const options = {
  scenarios: {
    steady: {
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: DURATION,
      // 預先配置足夠的 VU，否則到達率會被 VU 數卡住
      preAllocatedVUs: Math.min(Math.max(RATE, 10), 400),
      maxVUs: Math.min(Math.max(RATE * 2, 20), 800),
    },
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(95)', 'p(99)', 'max'],
};

const allowed = new Counter('rl_allowed');      // 2xx
const limited = new Counter('rl_limited');      // 429
const rejected = new Counter('rl_rejected');    // 409（庫存不足等業務拒絕）
const errored = new Counter('rl_error');
const limitedLatency = new Trend('rl_limited_latency');

export default function () {
  const userId = USER_MODE === 'shared'
    ? SHARED_USER_ID
    : String(__VU);

  const res = http.post(
    `${BASE_URL}/api/flash-sale/${PRODUCT_ID}`,
    JSON.stringify({ userId: Number(userId), quantity: 1 }),
    {
      headers: {
        'Content-Type': 'application/json',
        'X-User-Id': userId,
      },
      tags: { name: 'flash-sale-rate-limited' },
    },
  );

  if (res.status === 200 || res.status === 202) {
    allowed.add(1);
  } else if (res.status === 429) {
    limited.add(1);
    // 被拒絕的成本必須遠低於被處理的成本，
    // 否則限流本身就會變成瓶頸。這個數字用來驗證這件事。
    limitedLatency.add(res.timings.duration);
  } else if (res.status === 409) {
    rejected.add(1);
  } else {
    errored.add(1);
  }
}

function count(data, metric) {
  const m = data.metrics[metric];
  return m ? m.values.count : 0;
}

function trend(data, metric, stat) {
  const m = data.metrics[metric];
  return m ? m.values[stat] : null;
}

export function handleSummary(data) {
  const summary = {
    rate: RATE,
    duration: DURATION,
    userMode: USER_MODE,
    requests: count(data, 'http_reqs'),
    allowed: count(data, 'rl_allowed'),
    limited: count(data, 'rl_limited'),
    rejected: count(data, 'rl_rejected'),
    errored: count(data, 'rl_error'),
    actualRps: trend(data, 'http_reqs', 'rate'),
    durationMs: {
      avg: trend(data, 'http_req_duration', 'avg'),
      p95: trend(data, 'http_req_duration', 'p(95)'),
      p99: trend(data, 'http_req_duration', 'p(99)'),
    },
    limitedLatencyMs: {
      avg: trend(data, 'rl_limited_latency', 'avg'),
      p95: trend(data, 'rl_limited_latency', 'p(95)'),
    },
  };

  const out = {};
  out[SUMMARY_FILE] = JSON.stringify(summary, null, 2);
  return out;
}
