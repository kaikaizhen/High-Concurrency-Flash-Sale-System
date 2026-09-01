import http from 'k6/http';
import { Counter, Trend } from 'k6/metrics';

/**
 * Stage 9 — 統一壓測套件。
 *
 * 計畫 §14 的四種 Test Profile：
 *
 *   smoke   10 users            冒煙測試，確認功能正常、拿到基準延遲
 *   normal  100 users           日常負載
 *   stress  500 → 1000 → 5000   逐級加壓，找出容量上限
 *   spike   100 → 5000 → 100    瞬間尖峰，看系統崩不崩、恢不恢復
 *
 * 前三個階段的壓測腳本各自為政（race-condition.js / concurrency-control.js /
 * product-read.js / rate-limit.js），每個都只服務單一目的。
 * 這個套件把「負載模型」與「要打哪個端點」分開，
 * 同一套 Profile 可以套用在不同端點上，數據才有可比性。
 */

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5080';
const PROFILE = __ENV.PROFILE || 'smoke';
const SCENARIO = __ENV.SCENARIO || 'read';   // read | purchase
const PRODUCT_ID = __ENV.PRODUCT_ID;
const STRATEGY = __ENV.STRATEGY || 'Atomic';
const SUMMARY_FILE = __ENV.SUMMARY_FILE || 'summary.json';

// ---------------------------------------------------------------------
// Test Profile
// ---------------------------------------------------------------------
//
// 全部用 ramping-vus（逐步增減 VU）而不是一次拉滿：
// 瞬間開 5000 個 VU 會讓 k6 自己成為瓶頸，
// 量到的是「k6 能多快建立連線」而不是系統的容量。
// spike 是唯一的例外 —— 那正是它要測的東西。
const PROFILES = {
  smoke: {
    executor: 'ramping-vus',
    startVUs: 0,
    stages: [
      { duration: '5s', target: 10 },
      { duration: '20s', target: 10 },
      { duration: '5s', target: 0 },
    ],
  },

  normal: {
    executor: 'ramping-vus',
    startVUs: 0,
    stages: [
      { duration: '10s', target: 100 },
      { duration: '30s', target: 100 },
      { duration: '5s', target: 0 },
    ],
  },

  stress: {
    executor: 'ramping-vus',
    startVUs: 0,
    stages: [
      { duration: '10s', target: 500 },
      { duration: '20s', target: 500 },
      { duration: '10s', target: 1000 },
      { duration: '20s', target: 1000 },
      { duration: '15s', target: 5000 },
      { duration: '20s', target: 5000 },
      { duration: '10s', target: 0 },
    ],
  },

  spike: {
    executor: 'ramping-vus',
    startVUs: 0,
    stages: [
      { duration: '10s', target: 100 },
      { duration: '20s', target: 100 },   // 正常
      { duration: '5s', target: 5000 },   // 尖峰：5 秒內 50 倍
      { duration: '20s', target: 5000 },
      { duration: '5s', target: 100 },    // 回到正常
      { duration: '30s', target: 100 },   // 觀察是否恢復
      { duration: '5s', target: 0 },
    ],
  },
};

export const options = {
  scenarios: {
    main: PROFILES[PROFILE],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(50)', 'p(95)', 'p(99)', 'max'],
  // 壓測到 5000 VU 時大量請求會失敗，那是預期中的觀察對象，
  // 不該讓 k6 以非零狀態碼結束而讓執行器誤判為「執行失敗」。
  thresholds: {},
};

const ok = new Counter('req_ok');
const rejected = new Counter('req_rejected');   // 409 業務拒絕 / 429 限流
const failed = new Counter('req_failed');       // 5xx、連線失敗、逾時
const okLatency = new Trend('req_ok_latency');

export default function () {
  const res = SCENARIO === 'purchase' ? purchase() : read();

  if (res.status >= 200 && res.status < 300) {
    ok.add(1);
    okLatency.add(res.timings.duration);
  } else if (res.status === 409 || res.status === 429) {
    rejected.add(1);
  } else {
    failed.add(1);
  }
}

function read() {
  return http.get(`${BASE_URL}/api/products/${PRODUCT_ID}`, {
    tags: { name: 'product-read' },
  });
}

function purchase() {
  return http.post(
    `${BASE_URL}/api/flash-sale/${PRODUCT_ID}`,
    JSON.stringify({ userId: __VU, quantity: 1, strategy: STRATEGY }),
    {
      headers: {
        'Content-Type': 'application/json',
        'X-User-Id': String(__VU),
      },
      tags: { name: 'flash-sale' },
    },
  );
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
  const requests = count(data, 'http_reqs');
  const failures = count(data, 'req_failed');

  const summary = {
    profile: PROFILE,
    scenario: SCENARIO,
    productId: Number(PRODUCT_ID),
    requests: requests,
    ok: count(data, 'req_ok'),
    rejected: count(data, 'req_rejected'),
    failed: failures,
    errorRatePct: requests > 0
      ? Number(((failures / requests) * 100).toFixed(2))
      : 0,
    rps: trend(data, 'http_reqs', 'rate'),
    // 全部請求的延遲（含被快速拒絕的）
    durationMs: {
      avg: trend(data, 'http_req_duration', 'avg'),
      p50: trend(data, 'http_req_duration', 'p(50)'),
      p95: trend(data, 'http_req_duration', 'p(95)'),
      p99: trend(data, 'http_req_duration', 'p(99)'),
      max: trend(data, 'http_req_duration', 'max'),
    },
    // 只算成功請求 —— 被限流秒殺掉的 429 會把整體延遲拉得極低而失真
    okDurationMs: {
      avg: trend(data, 'req_ok_latency', 'avg'),
      p50: trend(data, 'req_ok_latency', 'p(50)'),
      p95: trend(data, 'req_ok_latency', 'p(95)'),
      p99: trend(data, 'req_ok_latency', 'p(99)'),
      max: trend(data, 'req_ok_latency', 'max'),
    },
  };

  const out = {};
  out[SUMMARY_FILE] = JSON.stringify(summary, null, 2);
  return out;
}
