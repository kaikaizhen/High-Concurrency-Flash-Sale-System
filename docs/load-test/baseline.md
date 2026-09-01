# Stage 9 — 壓測基準線

> Branch：`feature/load-test`
> 日期：2026-09-01
>
> 瓶頸分析見 [final.md](final.md)

---

## 1. 為什麼需要這一份

前七個階段各自寫了壓測腳本，但每一支都只為當時的問題服務：

| 腳本 | 目的 |
|---|---|
| `race-condition.js` | 重現超賣 |
| `concurrency-control.js` | 比較四種併發控制 |
| `product-read.js` | 量快取效果 |
| `rate-limit.js` | 驗證限流 |
| `idempotency.js` | 驗證重送保護 |

負載模型各不相同（`per-vu-iterations` / `shared-iterations` /
`constant-arrival-rate`），數字**彼此不可比**。

計畫 §14 要的是另一件事：

> 開始用數據討論效能，而不是「感覺很快」。

因此把「負載模型」與「要打哪個端點」拆開，
同一套 Profile 可以套用在不同端點上。

---

## 2. Test Profile

`flash-sale-suite.js` 提供四種，全部用 `ramping-vus`：

| Profile | VU 變化 | 目的 |
|---|---|---|
| `smoke` | 0 → 10 → 0 | 確認功能正常、取得基準延遲 |
| `normal` | 0 → 100 → 0 | 日常負載 |
| `stress` | 500 → 1000 → 5000 | 逐級加壓，找容量上限 |
| `spike` | 100 → **5000** → 100 | 5 秒內 50 倍，看崩不崩、恢不恢復 |

**刻意不用一次拉滿 VU。** 瞬間開 5000 個 VU 會讓 k6 自己成為瓶頸，
量到的是「k6 能多快建立連線」而不是系統容量。
`spike` 是唯一例外 —— 那正是它要測的東西。

### 兩種 Scenario

| Scenario | 端點 | 特性 |
|---|---|---|
| `read` | `GET /api/products/{id}` | 走 Redis 快取，幾乎不碰資料庫 |
| `purchase` | `POST /api/flash-sale/{id}` | 每次都要在同一列庫存上競爭 |

---

## 3. 測試環境

| 項目 | 內容 |
|---|---|
| API | ASP.NET Core 9，**Release**，單一 Instance（非 Docker） |
| 主機 | Windows 11，12 邏輯核心 |
| 資料庫 | SQL Server（遠端主機） |
| Redis / RabbitMQ | 遠端主機 |
| 壓測工具 | k6 v2.2.0，**與 API 同一台機器** |
| 限流 | **關閉**（`RateLimit__Enabled=false`） |

### 兩個必須先講清楚的前提

**限流關閉。** 不關的話量到的是限流器的行為而不是系統容量 ——
這是 Stage 7 文件已經記錄的取捨。

**k6 與 API 共用同一台機器的 12 核。** 兩者互相競爭 CPU，
因此 read 路徑的上限（約 31k RPS）有一部分是 k6 自己造成的，
真實容量應該更高。這個數字適合用來比較不同版本，
不適合當成「這個系統能撐多少」的絕對值。

---

## 4. 結果：讀取路徑

```powershell
.\tests\load\k6\Run-LoadTestSuite.ps1 -Profile smoke  -Scenario read
.\tests\load\k6\Run-LoadTestSuite.ps1 -Profile normal -Scenario read
.\tests\load\k6\Run-LoadTestSuite.ps1 -Profile stress -Scenario read
.\tests\load\k6\Run-LoadTestSuite.ps1 -Profile spike  -Scenario read
```

| Profile | 請求數 | 錯誤率 | RPS | Avg | P50 | P95 | P99 | Max |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| smoke | 225,285 | 0% | 7,509 | 1.1 ms | 0.8 ms | 2.0 ms | 6.0 ms | 279 ms |
| normal | 1,195,675 | 0% | 26,570 | 3.1 ms | 2.3 ms | 5.1 ms | 10.4 ms | 3,032 ms |
| stress | 3,271,026 | 0% | **31,151** | 43.3 ms | 15.7 ms | 96.9 ms | 204.3 ms | 3,574 ms |
| spike | 2,973,257 | 0% | 31,295 | 26.3 ms | 3.0 ms | 95.6 ms | 192.5 ms | 3,864 ms |

### 系統指標

| Profile | 峰值 CPU | 峰值記憶體 | 執行緒 | DB 命令數 | 快取命中率 |
|---|---:|---:|---:|---:|---:|
| smoke | 3.6% | 141 MB | 48 | 19 | 100% |
| normal | 12.1% | 177 MB | 59 | 28 | 100% |
| stress | 42.8% | 496 MB | 55 | 63 | 99.5% |
| spike | 42.8% | 560 MB | 54 | 58 | 99.8% |

**全部零錯誤。** 300 萬個請求、5000 個併發使用者，沒有任何失敗。

**DB 命令數 63 次 / 327 萬請求。** Stage 4 的快取在這個量級仍然有效。

**CPU 從未超過 43%。** 從 100 VU（26,570 RPS）到 5000 VU（31,151 RPS），
RPS 只多了 17%，但 P99 從 10.4 ms 漲到 204 ms —— 已經飽和，
而飽和點不是 CPU。

---

## 5. 結果：搶購路徑

```powershell
.\tests\load\k6\Run-LoadTestSuite.ps1 -Profile normal -Scenario purchase
.\tests\load\k6\Run-LoadTestSuite.ps1 -Profile stress -Scenario purchase
```

| Profile / 策略 | 請求數 | 成功 | 失敗 | 錯誤率 | RPS | P50 | P95 | P99 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| normal / `Atomic` | 6,363 | 6,363 | 0 | 0% | **141** | 629 ms | 850 ms | 930 ms |
| normal / `AtomicQueued` | 4,860 | 4,860 | 0 | 0% | **108** | 869 ms | 1,061 ms | 1,099 ms |
| stress / `Atomic` | 22,208 | 14,040 | **8,168** | **36.8%** | 187 | **15,002 ms** | 15,968 ms | 16,116 ms |

### 系統指標

| Profile / 策略 | 峰值 CPU | 峰值記憶體 | 峰值 DB 延遲 | DB 命令數 | 佇列峰值 |
|---|---:|---:|---:|---:|---:|
| normal / `Atomic` | **1.3%** | 421 MB | 92 ms | 12,749 | 0 |
| normal / `AtomicQueued` | **0.7%** | 705 MB | 7.8 ms | **4,883** | 4,837 |
| stress / `Atomic` | **2.4%** | 704 MB | **4,497 ms** | 28,099 | 0 |

`stress / Atomic` 的取樣有 11 次逾時 —— **連診斷端點自己都拿不到回應**。

---

## 6. 兩條路徑的對比

同一個系統、同一台機器、同樣的壓測工具：

| | read（有快取） | purchase（`Atomic`） | 倍數 |
|---|---:|---:|---:|
| RPS（stress） | 31,151 | 187 | **167×** |
| P99 | 204 ms | 16,116 ms | **79×** |
| 錯誤率 | 0% | 36.8% | — |
| **峰值 CPU** | **42.8%** | **2.4%** | **1/18** |

**慢 167 倍的那一條，CPU 用得少 18 倍。**

這一組數字就是整份報告的結論：瓶頸不在應用程式。
加 CPU、加記憶體、加 Instance 都不會改變 187 RPS 這個數字。

詳細分析見 [final.md](final.md)。

---

## 7. 非同步版本的取捨

`AtomicQueued` 在 100 VU 下比 `Atomic` **慢**（108 vs 141 RPS），
與 Stage 5 的結論一致 —— 發布訊息本身也要一次遠端往返。

但它把**資料庫寫入量降低 62%**（4,883 vs 12,749 次命令）：
API 只做扣庫存，建訂單交給 Worker。

資料庫是整個系統最難水平擴充的資源。
在資料庫已經是瓶頸的前提下，「用 API 的延遲換資料庫的負載」
通常是划算的。

佇列峰值 4,837 是本次測試沒有啟動 Worker 造成的 ——
那也正好展示了佇列的吸收能力。

---

## 8. 重現步驟

```powershell
# 1. 建置
dotnet build -c Release

# 2. 啟動 API，關閉限流
$env:ASPNETCORE_ENVIRONMENT="Development"
$env:RateLimit__Enabled="false"
dotnet run -c Release --no-build --project src/FlashSale.Api/FlashSale.Api.csproj --no-launch-profile --urls "http://localhost:5080"

# 3. 四種 Profile
.\tests\load\k6\Run-LoadTestSuite.ps1 -Profile smoke  -Scenario read
.\tests\load\k6\Run-LoadTestSuite.ps1 -Profile normal -Scenario read
.\tests\load\k6\Run-LoadTestSuite.ps1 -Profile stress -Scenario read
.\tests\load\k6\Run-LoadTestSuite.ps1 -Profile spike  -Scenario read

# 4. 搶購路徑
.\tests\load\k6\Run-LoadTestSuite.ps1 -Profile normal -Scenario purchase
.\tests\load\k6\Run-LoadTestSuite.ps1 -Profile stress -Scenario purchase
.\tests\load\k6\Run-LoadTestSuite.ps1 -Profile normal -Scenario purchase -Strategy AtomicQueued
```

每次執行會產生兩個檔案（皆已 gitignore）：

```text
tests/load/k6/results/suite-<label>-<時間>.json          k6 摘要
tests/load/k6/results/suite-<label>-<時間>-system.csv    每 2 秒的系統指標
```

系統指標是在壓測**期間**取樣的 —— 結束後才量會看到已經恢復的系統，
完全錯過瓶頸發生的那一刻。
