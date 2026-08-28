import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';

/**
 * Stage 2 — Race Condition 重現腳本。
 *
 * 目的不是驗證系統正確，而是**證明 Baseline 會超賣**。
 * 因此這裡刻意不設定會讓測試失敗的 threshold，
 * 所有回應（包含 500）都被歸類統計後留下紀錄。
 *
 * 每個 VU 只送出一次請求，讓 N 個請求盡可能同時打到 API，
 * 這樣「同時讀到同一個庫存值」的視窗才夠大。
 */

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5080';
const PRODUCT_ID = __ENV.PRODUCT_ID;
const VUS = Number(__ENV.VUS || 10);
const SUMMARY_FILE = __ENV.SUMMARY_FILE || 'summary.json';

export const options = {
  scenarios: {
    burst: {
      executor: 'per-vu-iterations',
      vus: VUS,
      iterations: 1,
      maxDuration: '3m',
    },
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  discardResponseBodies: false,
};

const success = new Counter('flash_sale_success');
const rejected = new Counter('flash_sale_rejected');
const errored = new Counter('flash_sale_error');

export default function () {
  const res = http.post(
    `${BASE_URL}/api/flash-sale/${PRODUCT_ID}`,
    JSON.stringify({ userId: __VU, quantity: 1 }),
    {
      headers: { 'Content-Type': 'application/json' },
      tags: { name: 'flash-sale' },
    },
  );

  if (res.status === 200) {
    success.add(1);
  } else if (res.status === 409) {
    // 庫存不足，這是系統「有意識地」拒絕
    rejected.add(1);
  } else {
    // 逾時、連線失敗、500…… 這些是壓力下暴露出的問題
    errored.add(1);
  }

  check(res, {
    'status is 200 or 409': (r) => r.status === 200 || r.status === 409,
  });
}

function count(data, metric) {
  const m = data.metrics[metric];
  if (!m) {
    return 0;
  }
  return m.values.count;
}

function trend(data, metric, stat) {
  const m = data.metrics[metric];
  if (!m) {
    return null;
  }
  return m.values[stat];
}

export function handleSummary(data) {
  const requests = count(data, 'http_reqs');

  const summary = {
    vus: VUS,
    productId: Number(PRODUCT_ID),
    requests: requests,
    success: count(data, 'flash_sale_success'),
    rejected: count(data, 'flash_sale_rejected'),
    errored: count(data, 'flash_sale_error'),
    httpFailed: count(data, 'http_req_failed'),
    durationMs: {
      avg: trend(data, 'http_req_duration', 'avg'),
      med: trend(data, 'http_req_duration', 'med'),
      p90: trend(data, 'http_req_duration', 'p(90)'),
      p95: trend(data, 'http_req_duration', 'p(95)'),
      p99: trend(data, 'http_req_duration', 'p(99)'),
      max: trend(data, 'http_req_duration', 'max'),
    },
  };

  const out = {};
  out[SUMMARY_FILE] = JSON.stringify(summary, null, 2);
  return out;
}
