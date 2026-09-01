# Stage 7 — Rate Limit

> Branch：`feature/rate-limit`
> 日期：2026-09-01
>
> 前置：[Stage 6 Idempotency](idempotency.md)

---

## 1. 這一階段的觀念轉換

前六個階段都在回答同一個問題：**如何處理更多請求**。

- Stage 3：如何在高併發下保持正確
- Stage 4：如何減少資料庫讀取
- Stage 5：如何把工作移出請求路徑
- Stage 6：如何避免重複執行

計畫 §12 提出的是另一個方向：

> 高併發架構不只是「處理更多 Request」，也包含**「拒絕不合理 Request」**。

有些請求不值得被服務。一個使用者每秒送 1000 次搶購，
他不會因為系統努力處理而買到更多 —— 庫存就那麼多。
系統唯一該做的是**用最低的成本拒絕他**，把資源留給其他人。

---

## 2. 三個維度的限制

計畫 §12 要求 per IP / per User / per Endpoint，三者各自解決不同的問題：

```text
            ┌──────────────────────────────────────────────┐
請求 ──────▶│ 全域 per-IP 限制（600 / 60s）                │
            │ 擋住：單一來源的洪水                          │
            └──────────────────────────────────────────────┘
                              │ 通過
                              ▼
            ┌──────────────────────────────────────────────┐
            │ 端點政策 per-User（僅套用在 /api/flash-sale）│
            │ 擋住：分散在多個來源、但屬於同一個人的洗版    │
            └──────────────────────────────────────────────┘
                              │ 通過
                              ▼
                        Controller
```

兩層**並存**，任一擋下就是 429。

**為什麼需要兩層。** 只有 per-IP：同一個人換幾台機器就能繞過。
只有 per-User：不帶使用者識別的匿名洪水完全不受限。

`/api/diagnostics` 明確標註 `[DisableRateLimiting]` ——
壓測腳本會高頻輪詢它（Stage 5 每秒取樣佇列長度）。
讓觀測工具被自己要觀測的限流機制擋下，量到的就不是系統的真實狀態了。

### 分區鍵

「分區」是計數的單位 —— 同一分區的請求共用一份額度。選錯的後果：

| 分區方式 | 後果 |
|---|---|
| 全部算在一起 | 一個人洗版就把所有人擋住 |
| 每個請求一區 | 等於沒有限流 |

`ForUser` 的退路設計：沒有 `X-User-Id` 時**退回 per-IP**，而不是不限制 ——
否則攻擊者只要不帶 Header 就能繞過。

> 目前使用者識別來自 Header，那是客戶端說了算的，換一個值就能繞過。
> 在有認證的系統中這應該來自 JWT claim。本專案尚未導入認證。

---

## 3. 測試方法

```powershell
.\tests\load\k6\Run-RateLimitTest.ps1 -Label SlidingWindow
```

| 情境 | 速率 | 使用者 | 預期 |
|---|---|---|---|
| `normal` | 10 req/s | 每個 VU 各自的 Id | 全部通過 |
| `abuse` | 1000 req/s | **同一個 Id** | 大部分被 429 |

k6 用 `constant-arrival-rate`（固定到達率）而不是固定 VU 數。
用固定 VU 的話，被拒絕會讓請求變快、反而送出更多請求，
量到的速率就不是設定的那個。

商品庫存設為 1,000,000：這一階段量的是限流，不是庫存耗盡。
庫存不足會回 409，混進來就分不清是誰擋下了請求。

四種演算法的比較是**分別重啟 API** 完成的，且期間關閉 per-IP 限制
以隔離變數 —— 否則全域限制會蓋過端點政策，四種演算法會量出一樣的結果。

---

## 4. 四種演算法的結果

設定：per-User，10 次 / 1 秒（Concurrency 為同時 10 個）。

### 正常流量（10 req/s，不同使用者）

| 演算法 | 請求 | 通過 | 429 | 通過率 |
|---|---:|---:|---:|---:|
| FixedWindow | 101 | 101 | 0 | 100% |
| SlidingWindow | 100 | 100 | 0 | 100% |
| TokenBucket | 101 | 101 | 0 | 100% |
| Concurrency | 101 | 101 | 0 | 100% |

**四種都不誤傷正常流量。** 這是前提 —— 一個會擋掉正常使用者的限流器
比沒有限流更糟。

### 濫用流量（1000 req/s，同一使用者，10 秒）

| 演算法 | 請求 | 通過 | 429 | 通過率 | 429 平均延遲 |
|---|---:|---:|---:|---:|---:|
| FixedWindow | 10001 | 100 | 9901 | 1.0% | 0.1 ms |
| SlidingWindow | 10001 | 90 | 9911 | 0.9% | 0.1 ms |
| TokenBucket | 10000 | 109 | 9891 | 1.1% | 0.1 ms |
| **Concurrency** | 10000 | **1544** | 8456 | **15.4%** | 0.1 ms |

前三者都收斂到 ~100 個（= 10/s × 10s），符合設定。

**Concurrency 是不同維度的東西。** 它限制的是「同時進行中的請求數」
而不是速率，所以放行量取決於每個請求處理多快 ——
請求越快，同樣的併發數就能吞下越多流量。

這不是缺點，是**用途不同**：

| | 管什麼 | 保護什麼 |
|---|---|---|
| Fixed / Sliding / TokenBucket | 多久內幾次 | 流量本身、公平性 |
| Concurrency | 同一時間幾個 | 下游資源的併發容量（連線池、執行緒） |

Stage 3 量到資料庫在同一列上只能容納有限的併發，
Concurrency Limiter 保護的正是那種資源。

### 拒絕的成本

| | 平均延遲 |
|---|---:|
| 正常處理的請求 | 12.7 ~ 17.0 ms |
| **被 429 拒絕的請求** | **0.1 ms** |

**拒絕成本是處理成本的 1/130 以下。**

這是限流有效的關鍵前提。限流器放在 Middleware 管線中、
Controller 之前，被擋下的請求不會碰到資料庫或 Redis。
若拒絕本身也要花力氣，限流器就會變成新的瓶頸。

---

## 5. 固定視窗的邊界爆發

這是四種演算法差異最戲劇性的地方，也是最容易被忽略的陷阱。

### 問題

固定視窗每 N 秒把計數歸零。攻擊者只要抓準時機：

```text
視窗 1                          視窗 2
├───────────────────────────┤├───────────────────────────┤
                    ████████  ████████
                    用滿額度  再用滿一次
                         ↑↑↑↑
                    短短一瞬間通過兩倍請求量
```

而**每個視窗看起來都完全符合規則**。

### 實測

```powershell
.\tests\load\k6\Test-WindowBoundary.ps1 -Label FixedWindow -PermitLimit 20 -WindowSeconds 10
```

限制：20 次 / 10 秒。

| | FixedWindow | SlidingWindow |
|---|---|---|
| T+0.0s 送 1 個 | 通過 1 | 通過 1 |
| T+9.1s 送 19 個（仍在視窗 1） | 通過 19，擋 0 | 通過 19，擋 0 |
| T+11.6s 送 20 個（已進入視窗 2） | **通過 20，擋 0** | **通過 1，擋 19** |
| 跨越邊界的約 3 秒內共放行 | **39 個** | **20 個** |
| 相對名目限制 | **1.95×** | **1.0×** |

FixedWindow 在 3.1 秒內放行了 39 個請求，而名目限制是「20 個 per 10 秒」。

SlidingWindow 把視窗切成 4 段逐段淘汰，
第二批送出時前一批仍在計算範圍內，因此正確地擋下 19 個。

### 代價

SlidingWindow 需要記住每個區段的計數（本專案 `SegmentsPerWindow = 4`），
記憶體是 FixedWindow 的 4 倍。段數越多越平滑，也越吃記憶體。

---

## 6. 全域 per-IP 限制

單獨測試（關閉 per-User 政策），限制 600 次 / 60 秒：

| 情境 | 請求 | 通過 | 429 |
|---|---:|---:|---:|
| normal（10/s，不同使用者） | 101 | 101 | 0 |
| abuse（1000/s） | 9957 | **499** | 9458 |

101 + 499 = **600**，恰好是 60 秒視窗的額度。

注意 `normal` 情境雖然用不同的使用者 Id，**仍然算在同一個 IP 分區**裡 ——
壓測都從 localhost 發出。這正是 per-IP 與 per-User 的差別。

---

## 7. 演算法選擇

> **選定 SlidingWindow 作為搶購端點的預設演算法。**

| 演算法 | 適用 | 不適用 |
|---|---|---|
| **FixedWindow** | 全域粗粒度保護（額度遠高於正常流量，邊界問題無關緊要） | 需要精確控制的端點 |
| **SlidingWindow** | 需要精確速率控制的端點 —— **搶購** | 記憶體極度受限的場景 |
| **TokenBucket** | 平均速率要限制、但短暫突發是正常行為（例如批次 API） | 秒殺 —— 突發正是要防的東西 |
| **Concurrency** | 保護下游資源的併發容量（連線池） | 當成速率限制使用 |

實際採用的組合：

- 全域 per-IP：**FixedWindow** 600/60s —— 粗粒度，邊界爆發到 1200/60s 也無妨
- 搶購端點 per-User：**SlidingWindow** 10/1s —— 精確，不給邊界爆發的空間

**為什麼秒殺不用 TokenBucket。** 它是唯一刻意允許突發的演算法 ——
桶裡累積的權杖可以一次用掉。那對「平均速率合理但偶爾要爆發」的
批次呼叫很好，但秒殺場景下突發正是我們要防的東西。

---

## 8. 對先前階段壓測的影響

**限流預設開啟，這會擋下 Stage 2–6 的壓測腳本。**

那些腳本從單一來源送出數千個請求，全域 per-IP 限制（600/60s）
會在第 600 個之後開始回 429。

要重現先前階段的結果：

```powershell
$env:RateLimit__Enabled = "false"
```

這不是缺陷。**加了限流之後，壓測本身就必須考慮它的存在** ——
否則量到的是限流器的行為而不是系統的容量。
正式環境的壓力測試也一樣要面對這個問題（通常是把壓測來源加入白名單）。

---

## 9. 已知限制

### 限流狀態存在單一 Instance 的記憶體中

ASP.NET Core 的 RateLimiter 是行程內的。Stage 8 導入多 Instance 之後，
N 台機器各有一份獨立的額度，**實際限制會變成 N 倍**。

要正確處理需要分散式限流（例如 Redis 上的計數器或 Lua 腳本）。
這與 Stage 4 的 `KeyedLock`、`InMemoryMetricsCollector` 是同一類問題，
都留給 Stage 8。

### 反向代理會讓所有請求變成同一個 IP

`RemoteIpAddress` 在反向代理後面拿到的是代理的 IP。
Stage 8 導入 Nginx 之後必須設定 `ForwardedHeaders` 中介軟體，
否則**全部流量會被算成同一個分區**，per-IP 限制會誤傷所有人。

已在 `RateLimitPartitionKeys` 的註解中標記。

### 使用者識別可被偽造

`X-User-Id` 是客戶端說了算的。攻擊者換一個值就能取得新的額度。
per-IP 那一層仍然有效，但 per-User 這層在沒有認證的情況下只能防呆、不能防惡意。

---

## 10. 重現步驟

```powershell
# 1. 建置並啟動 API（關閉 per-IP 以隔離端點政策）
dotnet build -c Release
$env:ASPNETCORE_ENVIRONMENT="Development"
$env:RateLimit__PerIp__Enabled="false"
$env:RateLimit__FlashSale__Algorithm="SlidingWindow"   # 或 FixedWindow / TokenBucket / Concurrency
dotnet run -c Release --no-build --project src/FlashSale.Api/FlashSale.Api.csproj --no-launch-profile --urls "http://localhost:5080"

# 2. 正常 vs 濫用
.\tests\load\k6\Run-RateLimitTest.ps1 -Label SlidingWindow

# 3. 邊界爆發（需重啟 API 並改為 20/10s）
$env:RateLimit__FlashSale__PermitLimit="20"
$env:RateLimit__FlashSale__WindowSeconds="10"
.\tests\load\k6\Test-WindowBoundary.ps1 -Label FixedWindow -PermitLimit 20 -WindowSeconds 10

# 4. 全域 per-IP（需重啟 API）
$env:RateLimit__PerIp__Enabled="true"
$env:RateLimit__FlashSale__Enabled="false"
.\tests\load\k6\Run-RateLimitTest.ps1 -Label PerIpOnly
```

手動觸發 429：

```bash
for i in $(seq 1 30); do
  curl -s -o /dev/null -w "%{http_code} " \
    -X POST http://localhost:5080/api/flash-sale/1 \
    -H "Content-Type: application/json" \
    -H "X-User-Id: 42" \
    -d '{"userId":42,"quantity":1}'
done
```
