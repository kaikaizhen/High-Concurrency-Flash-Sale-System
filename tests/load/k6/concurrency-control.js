import http from 'k6/http';
import { Counter } from 'k6/metrics';

/**
 * Stage 3 — Concurrency Control 比較腳本。
 *
 * 與 Stage 2 的差別：
 *
 * Stage 2 用 per-vu-iterations（每個 VU 送 1 次），目的是把「同時抵達」
 * 的視窗放到最大以重現 Race Condition。但那個模型在 500 VU 以上會撞到
 * Kestrel 的 accept backlog，293/789 個請求根本沒進到應用程式，
 * 量到的 Latency 也就沒有意義。
 *
 * Stage 3 要比較的是三種做法的**正確性與效能代價**，因此改用
 * shared-iterations：固定 VU 數（= 同時連線數），5000 次請求分攤下去。
 * 連線數壓在連線層撐得住的範圍內，所有請求都真正進到應用程式，
 * 而競爭同一列庫存的壓力完全不減。
 */

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5080';
const PRODUCT_ID = __ENV.PRODUCT_ID;
const STRATEGY = __ENV.STRATEGY || 'Atomic';
const VUS = Number(__ENV.VUS || 200);
const ITERATIONS = Number(__ENV.ITERATIONS || 5000);
const SUMMARY_FILE = __ENV.SUMMARY_FILE || 'summary.json';

export const options = {
  scenarios: {
    contention: {
      executor: 'shared-iterations',
      vus: VUS,
      iterations: ITERATIONS,
      maxDuration: '10m',
    },
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

const success = new Counter('flash_sale_success');
const rejected = new Counter('flash_sale_rejected');
const errored = new Counter('flash_sale_error');

export default function () {
  const res = http.post(
    `${BASE_URL}/api/flash-sale/${PRODUCT_ID}`,
    JSON.stringify({ userId: __VU, quantity: 1, strategy: STRATEGY }),
    {
      headers: { 'Content-Type': 'application/json' },
      tags: { name: 'flash-sale' },
    },
  );

  if (res.status === 200) {
    success.add(1);
  } else if (res.status === 409) {
    // 庫存不足，或樂觀鎖重試次數用盡 —— 都是系統有意識地拒絕
    rejected.add(1);
  } else {
    errored.add(1);
  }
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
  const summary = {
    strategy: STRATEGY,
    vus: VUS,
    productId: Number(PRODUCT_ID),
    requests: count(data, 'http_reqs'),
    success: count(data, 'flash_sale_success'),
    rejected: count(data, 'flash_sale_rejected'),
    errored: count(data, 'flash_sale_error'),
    rps: trend(data, 'http_reqs', 'rate'),
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
