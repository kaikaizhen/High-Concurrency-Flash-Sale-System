import http from 'k6/http';
import { Counter } from 'k6/metrics';

/**
 * Stage 4 — 商品讀取壓測。
 *
 * 為什麼壓 GET /api/products/{id} 而不是搶購？
 *
 * Stage 3 選定的 Atomic Update 在成功路徑上**完全不讀取商品**，
 * 它只送一個 UPDATE。所以替商品加快取對搶購的寫入路徑毫無幫助 ——
 * 寫入無法用快取解決，那是 Stage 5（Queue）的題目。
 *
 * 真正該快取的是讀取路徑：秒殺開始前所有人都在刷新商品頁。
 * 這才是「用 Redis 減少昂貴資源存取」真正發生作用的地方。
 */

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5080';
const PRODUCT_ID = __ENV.PRODUCT_ID;
const VUS = Number(__ENV.VUS || 200);
const ITERATIONS = Number(__ENV.ITERATIONS || 5000);
const SUMMARY_FILE = __ENV.SUMMARY_FILE || 'summary.json';

export const options = {
  scenarios: {
    read: {
      executor: 'shared-iterations',
      vus: VUS,
      iterations: ITERATIONS,
      maxDuration: '10m',
    },
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

const ok = new Counter('product_read_ok');
const notFound = new Counter('product_read_not_found');
const errored = new Counter('product_read_error');

export default function () {
  const res = http.get(`${BASE_URL}/api/products/${PRODUCT_ID}`, {
    tags: { name: 'product-read' },
  });

  if (res.status === 200) {
    ok.add(1);
  } else if (res.status === 404) {
    // Cache Penetration 實驗會刻意打不存在的 Id
    notFound.add(1);
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
    vus: VUS,
    productId: Number(PRODUCT_ID),
    requests: count(data, 'http_reqs'),
    ok: count(data, 'product_read_ok'),
    notFound: count(data, 'product_read_not_found'),
    errored: count(data, 'product_read_error'),
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
