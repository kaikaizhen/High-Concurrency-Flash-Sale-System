import http from 'k6/http';
import { Counter } from 'k6/metrics';

/**
 * Stage 6 — 併發重複請求測試。
 *
 * 計畫 §11 要求「Concurrent Duplicate Test」。
 *
 * 所有 VU **同時**送出帶著**完全相同 Idempotency-Key** 的請求。
 * 這是最嚴苛的情況：沒有任何時間差讓第一個請求先完成並寫下記錄，
 * 全部都在「還沒有人完成」的瞬間抵達。
 *
 * 正確的系統必須：
 *   - 恰好建立 1 筆訂單
 *   - 庫存恰好減少 1
 *   - 其餘請求收到 409（處理中）或回放的 200/202
 */

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5080';
const PRODUCT_ID = __ENV.PRODUCT_ID;
const STRATEGY = __ENV.STRATEGY || 'Atomic';
const IDEMPOTENCY_KEY = __ENV.IDEMPOTENCY_KEY;
const VUS = Number(__ENV.VUS || 50);
const SUMMARY_FILE = __ENV.SUMMARY_FILE || 'summary.json';

export const options = {
  scenarios: {
    duplicates: {
      // 每個 VU 只送一次，讓它們盡可能同時抵達
      executor: 'per-vu-iterations',
      vus: VUS,
      iterations: 1,
      maxDuration: '2m',
    },
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(95)', 'max'],
};

const accepted = new Counter('idem_accepted');    // 200 / 202
const replayed = new Counter('idem_replayed');    // 回放（帶 Idempotency-Replayed 標頭）
const conflict = new Counter('idem_conflict');    // 409 處理中
const errored = new Counter('idem_error');

export default function () {
  const res = http.post(
    `${BASE_URL}/api/flash-sale/${PRODUCT_ID}`,
    JSON.stringify({ userId: __VU, quantity: 1, strategy: STRATEGY }),
    {
      headers: {
        'Content-Type': 'application/json',
        'Idempotency-Key': IDEMPOTENCY_KEY,
      },
      tags: { name: 'flash-sale-idempotent' },
    },
  );

  if (res.status === 200 || res.status === 202) {
    accepted.add(1);

    if (res.headers['Idempotency-Replayed'] === 'true') {
      replayed.add(1);
    }
  } else if (res.status === 409) {
    conflict.add(1);
  } else {
    errored.add(1);
  }
}

function count(data, metric) {
  const m = data.metrics[metric];
  return m ? m.values.count : 0;
}

export function handleSummary(data) {
  const summary = {
    vus: VUS,
    productId: Number(PRODUCT_ID),
    idempotencyKey: IDEMPOTENCY_KEY,
    requests: count(data, 'http_reqs'),
    accepted: count(data, 'idem_accepted'),
    replayed: count(data, 'idem_replayed'),
    conflict: count(data, 'idem_conflict'),
    errored: count(data, 'idem_error'),
  };

  const out = {};
  out[SUMMARY_FILE] = JSON.stringify(summary, null, 2);
  return out;
}
