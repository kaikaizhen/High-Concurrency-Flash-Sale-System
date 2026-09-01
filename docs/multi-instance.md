# Stage 8 — Multi Instance

> Branch：`feature/multi-instance`
> 日期：2026-09-01
>
> 前置：[Stage 4 Redis](load-test/redis.md)、[Stage 7 Rate Limit](rate-limit.md)

---

## 1. 這一階段要面對什麼

前面七個階段都跑在**單一 Instance** 上。那個前提讓三處程式碼看起來完全正確：

| 元件 | 階段 | 單機時 | 多機時 |
|---|---|---|---|
| `InMemoryMetricsCollector` | Stage 4 | 正確 | 每台只看得到自己的數字 |
| `KeyedLock`（Single Flight） | Stage 4 | 正確 | N 台 = N 個獨立的鎖，保護降為 1/N |
| ASP.NET 內建 RateLimiter | Stage 7 | 正確 | N 台 = N 份額度，**實際限制變成 N 倍** |

三處都在當時的註解裡標記過「這是 Stage 8 的問題」。現在把它們一次解決。

計畫 §13 的核心：

```text
不能依賴 static Dictionary / static int / MemoryCache / Local Session
作為跨 Server 共享狀態，因為 API-1 Memory != API-2 Memory
```

---

## 2. 架構

```text
                        Nginx :8080
                     (least_conn 負載平衡)
                            │
              ┌─────────────┼─────────────┐
              ▼             ▼             ▼
           api-1         api-2         api-3
              │             │             │
              └─────────────┼─────────────┘
                            │
              ┌─────────────┼─────────────┐
              ▼             ▼             ▼
         SQL Server       Redis       RabbitMQ
        （共用資料）  （共用狀態）  （共用佇列）
```

三個 API 容器**完全相同**，只有 `INSTANCE_ID` 不同 ——
這正是 Stateless 的意思：任何一台都能處理任何請求，
沒有哪台「擁有」特定的狀態。

### 觀察的前提：X-Instance-Id

`InstanceHeaderMiddleware` 在每個回應加上 `X-Instance-Id`。
沒有它就看不出負載平衡有沒有生效，
也就無法判斷任何行為差異是不是分散造成的。

`/api/diagnostics/metrics` 另外回傳 `instanceId` 與 `scope`，
後者標示這份數字是「共用的」還是「只有這台的」。

---

## 3. 先看到問題

`.env` 把三個開關設為 false，重現 Stage 7 結束時的狀態：

```powershell
SHARED_METRICS=false
SHARED_LOCK=false
SHARED_RATELIMIT=false
```

```powershell
docker compose up -d
.\tests\load\k6\Run-MultiInstanceTest.ps1
```

### 3.1 計數器各自為政

60 個併發請求讀同一個商品後，從三台分別讀計數器：

```text
api-1    DbCommands=36     CacheHits=39     Scope=InMemory (this instance only)
api-2    DbCommands=0      CacheHits=20     Scope=InMemory (this instance only)
api-3    DbCommands=93     CacheHits=59     Scope=InMemory (this instance only)
```

**同一個問題有三個答案，而且都是錯的。**

`api-2` 回報 `DbCommands=0` 特別危險 —— 它不是「沒有查詢」，
是「這台沒有查詢」。負載平衡剛好沒把冷快取的請求分給它，
於是監控看起來一片祥和。

先前階段所有「DB 查詢數」的量測，在多 Instance 下全部失效。

### 3.2 Single Flight 失效

Stage 4 的成果是「冷快取 200 個併發請求只造成 1 次資料庫查詢」。
多 Instance 之後：

```text
冷快取造成的 DB 查詢數 : 93   （單台的數字，三台加總更多）
```

原因：`KeyedLock` 用的是行程內的 `SemaphoreSlim`。
三台機器各有一個「product:42 的鎖」，彼此互不知情，
於是**每台都會派一個代表去查資料庫**。

### 3.3 限流額度變成 N 倍

單一使用者持續 100 req/s、3 秒，限制是 10 次/秒：

```text
請求 301   通過 90   被擋 211
```

**90 = 10/秒 × 3 台 × 3 秒。** 額度精準地變成三倍。

使用者只要重試就會被分到不同機器，實際上能用的額度是設定值的 N 倍。
機器越多，限流越沒有意義。

---

## 4. 解法

### 4.1 共用計數器：`RedisMetricsCollector`

用單一 Redis Hash 存四個計數，任何一台讀到的都是全貌。

計數用 **fire-and-forget**：觀測數據不值得讓每個資料庫查詢
都多等一次 Redis 回應。少數幾次計數遺失不影響
「5000 次請求打了幾次資料庫」這種量級的判斷。

### 4.2 分散式鎖：`RedisKeyedLock`

```text
SET key token NX PX 10000     取得鎖（NX 保證只有一個人成功）
                              ↓
                       查資料庫、寫回快取
                              ↓
Lua: if GET key == token then DEL key      釋放
```

三個關鍵：

**TTL 是必要的。** 持有者當機時鎖必須自己過期，否則整個 Key 永久卡死。

**釋放要用 Lua 比對 token。** 直接 `DEL` 的話，
若自己的鎖已因逾時被別人取得，就會誤刪別人的鎖。
「GET 比對 → DEL」拆成兩步也不行 —— 兩者之間鎖可能剛好過期並被別人取得。

**等不到鎖就放行。** 等待上限 3 秒，逾時直接去查資料庫。
讓使用者多一次資料庫查詢，好過讓他一直等。
Redis 掛掉時同理 —— 退化成「不上鎖」而不是讓請求失敗。

### 4.3 分散式限流：`RedisSlidingWindowRateLimiter`

ASP.NET Core 內建的 RateLimiter 無法改造：
它的擴充點 `RateLimiter.AttemptAcquireCore` 是**同步**的，
無法在裡面呼叫 Redis。因此改以中介軟體實作，
內建的那一個在分散式模式下就不註冊，避免兩層限流疊加。

演算法是 Redis Sorted Set 的滑動視窗：
每個請求以時間戳為 score 存入，判斷時先移除視窗外的成員再計數。

**整段邏輯必須是一個 Lua 腳本。**
「清理 → 計數 → 決定 → 寫入」拆成多次往返的話，
兩個併發請求會同時讀到相同的計數而雙雙放行 ——
這與 Stage 2 的 Read-Modify-Write 是完全相同的錯誤。

Redis 掛掉時**放行**而不是全部拒絕：
限流是保護機制而不是業務規則，
讓它的故障演變成全站不可用，比暫時失去保護更糟。

### 4.4 反向代理的真實 IP

Stage 7 標記的陷阱：`RemoteIpAddress` 在 Nginx 後面會是 Nginx 的 IP，
per-IP 限流會把**所有人**算成同一個分區 —— 一個人超量就擋住全站。

解法是 Nginx 送出 `X-Forwarded-For`，API 端用 `UseForwardedHeaders()` 還原。
`KnownProxies` 清空代表信任所有上游，
**前提是 API 不會被直接對外暴露**，只能經由 Nginx 進來。

---

## 5. 修好之後

`.env` 三個開關改為 true，重跑同一套測試：

| 檢查項目 | 行程內狀態 | Redis 共用狀態 |
|---|---|---|
| 三台回報的 `DbCommands` | 36 / 0 / 93 | **3 / 3 / 3** |
| 數字是否一致 | ❌ 各自為政 | ✅ 共用 |
| 冷快取造成的 DB 查詢 | 93（單台） | **3** |
| 限流通過數（10/秒 × 3 秒） | **90（3 倍）** | **30（正確）** |

Single Flight 的 3 次而不是 1 次：三台機器各自搶 Redis 鎖，
第一台拿到後其餘兩台等待，但等待期間快取尚未寫入完成，
逾時或搶到鎖後仍會各查一次。從 93 降到 3 已經是 31 倍的改善，
要壓到 1 需要更長的鎖等待時間，代價是延遲。

---

## 6. Stateless 驗證

同一個邏輯流程跨機器接手是否仍然正確：

```text
第一次搶購 -> api-2  (HTTP 200)
重送 6 次   -> 分散到 3 台，其中 6 次為回放

訂單數 : 1   (預期 1)
庫存   : 99   (預期 99)
結果   : PASS
```

帶同一個 `Idempotency-Key` 的重送落到了**不同的機器**，
但每一台都能認出「這個請求已經處理過」並回放原本的回應。

因為 Stage 6 的冪等記錄存在 Redis，而不是各機器的記憶體。
若當初存在 `MemoryCache`，這裡就會建立多筆訂單。

---

## 7. Kill Instance

```text
已停止 flashsale-api-2

api-1      15 次
api-3      15 次
失敗請求數 : 0   (預期 0)
```

流量自動重新分配到剩下的兩台，**沒有任何請求失敗**。

靠的是 Nginx 的 `max_fails=2 fail_timeout=5s` ——
連續失敗就暫時把該台移出輪替。
沒有它，Nginx 會一直把 1/3 的流量送給已經不存在的容器。

### `proxy_next_upstream` 刻意不含 POST 重送

```nginx
proxy_next_upstream error timeout http_502 http_503 http_504;
```

沒有加 `non_idempotent`。POST 重送可能造成重複執行 ——
Nginx 無法知道「請求有沒有真的被處理」，只知道沒收到回應。

搶購的重送保護由 Stage 6 的 `Idempotency-Key` 負責，
那是應用層才有足夠資訊做的判斷。

### 健康檢查刻意不檢查資料庫

`/health` 只回報「這個行程活著」，不檢查 SQL Server 或 Redis。

那些是**共用相依**：一掛就會讓所有 Instance 同時被判定為不健康
而全部下線，反而製造出比原本更嚴重的故障。

---

## 8. 過程中修正的問題

### 8.1 k6 腳本把限流分區鍵塞進 body

第一次量測限流時看到「通過 0、被擋 211、其餘 90 個歸類為錯誤」。

原因是 `rate-limit.js` 把 `X-User-Id` 的值 `Number()` 之後放進 body 的 `userId`。
Stage 7 的分區鍵是 `"9999"` 所以沒事，Stage 8 改用 `"rl-a1b2c3d4"` 這種字串，
`Number()` 得到 `NaN`、序列化成 `null`，被模型驗證擋下回 **400**。

那些 400 被計為「錯誤」，看起來像限流完全失效。

`X-User-Id`（限流分區）與 body 的 `userId`（商業使用者）本來就是兩回事，
已改為 body 使用 `__VU`。

### 8.2 PowerShell `Start-Job` 無法製造瞬間爆發

限流測試原本用 60 個 `Start-Job` 並行送出，結果 60 個全部通過。

`Start-Job` 每一個都要啟動一個新的 PowerShell 行程（各數百毫秒），
「並行」的請求實際上散布在好幾秒內、跨越多個限流視窗，
於是量不出「額度變成 N 倍」。已改用 k6。

---

## 9. 已知限制

### Docker Desktop 的埠轉發是壓測瓶頸

k6 以 100 req/s 打 `localhost:8080` 時，部分連線會被重設。
本階段的量測都控制在這個門檻以下。

要做真正的高流量壓測，應該在容器網路內執行 k6，
或直接對某一台 API 容器施壓。

### 分散式限流依賴時鐘同步

滑動視窗的時間戳由各 Instance 提供，
前提是它們的時鐘大致同步（NTP）。偏差 X 毫秒會讓視窗邊界飄移 X 毫秒。

秒級視窗下幾十毫秒的偏差無關緊要，
但若偏差達到視窗長度的量級就會失準。

另一個選擇是在 Lua 內呼叫 `redis.call('TIME')` 取得單一時間來源，
代價是腳本變成非確定性指令，對某些 Redis 部署模式有額外限制。

### Worker 尚未多 Instance

`FlashSale.Worker` 目前只有一個。RabbitMQ 的競爭消費者模式
本來就支援多個 Worker 分攤同一個佇列，
且 Stage 6 的去重（`Order.IdempotencyKey` 唯一索引）已經能防止重複建單，
理論上直接增加副本即可。本階段未實測。

---

## 10. 重現步驟

```powershell
# 1. 準備連線設定
cp .env.example .env
#    填入 SQL Server / Redis / RabbitMQ 的實際位址
#    容器內看不到 localhost —— 服務在 Docker 主機上時要用 host.docker.internal

# 2. 建置並啟動叢集
docker compose up -d --build

# 3. 確認負載平衡
curl -i http://localhost:8080/api/products      # 看 X-Instance-Id

# 4. 完整驗證（含 Kill Instance）
.\tests\load\k6\Run-MultiInstanceTest.ps1

# 5. 重現「行程內狀態」的問題
#    把 .env 的三個開關改為 false 後
docker compose up -d
.\tests\load\k6\Run-MultiInstanceTest.ps1 -SkipKillTest

# 6. 收工
docker compose down
```
