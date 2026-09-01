# 最終分析報告

> Branch：`feature/observability-optimization`
> 日期：2026-09-01
>
> 計畫 §15「最終輸出」要求的報告。

---

## 1. 可觀測性：從「猜」到「定位」

計畫 §15 的目標：

```text
出問題 → 猜        變成        出問題 → Metrics / Logs / Tracing → 定位瓶頸
```

三者分工不同，缺一不可：

| | 回答的問題 | 本專案的實作 |
|---|---|---|
| **Metrics** | 系統現在健不健康？ | OpenTelemetry + 自訂 Meter |
| **Logs** | 哪些請求出了問題？ | Serilog 結構化日誌，一請求一行 |
| **Tracing** | **這一個**請求為什麼慢？ | OpenTelemetry Traces + 自訂 Span |

### 1.1 結構化日誌

計畫指定的欄位（RequestId / UserId / ProductId / Duration / Result / Exception）
全部落在**一行**裡：

```text
[12:52:58 INF] HTTP POST /api/flash-sale/98 responded 200 in 84.01 ms.
  TraceId=8d5996e0f89db35334244dc19af81ad0 UserId=obs-1 ProductId=98
  IdempotencyKey=null Instance=DESKTOP-ADG9RR6
```

**一個請求一行，不是每個步驟一行。** 散落各處的日誌無法回答
「這個請求花了多久、結果是什麼」—— 得先把它們串起來，
而那正是 TraceId 存在的理由。這一行的用途是**找出哪些請求有問題**，
找到之後拿 TraceId 去 Tracing 看它內部每一段的耗時。

**日誌等級隨結果變化**，不是一律 Information：

| 狀態 | 等級 | 理由 |
|---|---|---|
| 5xx | Error | 系統的問題 |
| 4xx | Warning | 請求的問題（庫存不足、限流、重複請求） |
| 其餘 | Information | 正常 |

全部記成 Information 的話，出事時得自己用眼睛在幾百萬行裡找異常。

檔案輸出用 JSON（`logs/flashsale-YYYYMMDD.jsonl`）：

```json
{"@t":"2026-09-01T04:52:58.5260686Z","@l":"Warning",
 "@tr":"18fb12a26a9cd16826f05277aeb2d945","@sp":"5d3d9a7ff68f0ccb",
 "RequestPath":"/api/flash-sale/999999","StatusCode":404,
 "DurationMs":7.1322,"ProductId":"999999","InstanceId":"DESKTOP-ADG9RR6"}
```

`@tr` / `@sp` 是 TraceId 與 SpanId ——
從一行慢請求的日誌可以直接跳到它的完整 Trace。
沒有這個關聯，日誌與追蹤就是兩座孤島。

主控台給人看（可讀格式）、檔案給機器解析（JSON）。
兩者用同一種格式，必然有一邊不好用。

### 1.2 指標

內建 instrumentation 提供**技術指標**（HTTP 請求數／延遲／錯誤數、
DB 命令耗時、GC 與執行緒池）。自訂 Meter 補上**業務指標**：

| 指標 | 型別 | 標籤 |
|---|---|---|
| `flashsale.purchase.attempts` | Counter | strategy |
| `flashsale.orders.created` | Counter | strategy, queued |
| `flashsale.purchase.rejected` | Counter | strategy, **reason** |
| `flashsale.cache.lookups` | Counter | result (hit/miss) |
| `flashsale.purchase.duration` | Histogram | strategy |

**為什麼需要業務指標。**「HTTP 200 的比例」回答不了「有沒有人買到東西」——
秒殺賣完之後全部回 409，技術指標看起來一切正常，業務上卻已經結束了。

**`reason` 標籤是 rejected 這個指標的價值所在。**
「拒絕數上升」本身沒有資訊量：庫存賣完是正常的，
樂觀鎖重試用盡代表系統過載，兩者的處置完全不同。

拒絕原因刻意歸類成少數幾個固定值而不是直接用 Exception 訊息 ——
那會造成**高基數**問題：每個不同字串都產生一組新的時序資料，
訊息若含 Id 或數值，指標系統會被撐爆。

`orders.created` 用 Counter 而不是 Gauge：Counter 只增不減，
任何時間區間的差值就是那段期間的成交量（也就是計畫要的 **Orders/sec**），
重啟或取樣遺漏都不會讓數字失真。

### 1.3 追蹤

內建 instrumentation 會產生「HTTP 請求」與「SQL 命令」兩層 Span，
但**中間是空的** —— 看得到「請求花了 900ms」和「這個 UPDATE 花了 5ms」，
卻看不出剩下的 895ms 花在哪裡。

自訂 Span 補上業務層：

```text
HTTP POST /api/flash-sale/{id}          ← AspNetCore instrumentation
└── FlashSale Purchase Atomic           ← 自訂（strategy, product_id, result）
    ├── UPDATE Products                 ← SqlClient instrumentation
    └── INSERT Orders
```

刻意**不記錄 SQL 語句文字**：參數值可能含個資，
而「哪一段慢」從 Span 的耗時與呼叫關係就看得出來。

**取樣率是可設定的，壓測時務必調低**（容器組態設為 5%）。
每個請求都產生完整 Trace 的話，觀測本身會成為瓶頸 ——
而觀測系統的第一守則就是不能改變被觀測的對象。

### 1.4 儀表板

Aspire Dashboard 一個容器同時接收 Traces / Metrics / Logs（OTLP），
不需要另外架 Jaeger + Prometheus + Grafana 三套。

```powershell
docker compose up -d aspire-dashboard    # http://localhost:18888
```

資料只存在記憶體中，重啟就消失 —— 適合開發與壓測分析，
正式環境需要有持久化的後端。

---

## 2. 優化：Measure → Hypothesis → Change → Measure Again

計畫 §15 明令**禁止憑感覺優化**。以下是完整的推導過程。

### 2.1 Measure

Stage 9 的數據（[final.md](load-test/final.md)）：

| | read（有快取） | purchase | 倍數 |
|---|---:|---:|---:|
| RPS | 31,151 | 187 | 167× |
| 峰值 CPU | 42.8% | **2.4%** | 1/18 |

### 2.2 Find Bottleneck

慢 167 倍的路徑 CPU 用得少 18 倍 —— 系統不是忙不過來，是在等。
逐項排除後確認：**`Products` 表上那一列的排他鎖**。

### 2.3 Hypothesis

看 `AtomicFlashSalePurchaseStrategy` 實際送出的命令：

```text
BEGIN TRANSACTION      ← 往返 1
UPDATE Products ...    ← 往返 2  ← 排他鎖從這裡開始
INSERT INTO Orders ... ← 往返 3
COMMIT                 ← 往返 4  ← 鎖到這裡才釋放
```

**鎖被持有的期間橫跨三次網路往返**（SQL Server 在遠端主機）。
秒殺時所有人搶同一列，那段時間就是全系統的序列化瓶頸。

假設：把整段合併成**單一批次語句**，鎖只在伺服器端執行期間持有，
臨界區應大幅縮短，吞吐量隨之提升。

### 2.4 Change

`AtomicBatchedFlashSalePurchaseStrategy` +
`IProductRepository.TryPurchaseInSingleRoundTripAsync`：

```sql
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;
    UPDATE Products SET Stock = Stock - @quantity
    WHERE Id = @productId AND Stock >= @quantity;

    IF @@ROWCOUNT = 1
    BEGIN
        INSERT INTO Orders (...) VALUES (...);
        SELECT CAST(SCOPE_IDENTITY() AS int);
    END
    ELSE SELECT CAST(0 AS int);
COMMIT TRANSACTION;
```

**語意與原版完全相同** —— 一樣的正確性保證、一樣的冪等防護、
一樣的回應碼。差別只在往返次數。這是刻意的：
優化不該改變行為，否則量到的差異分不清是「變快了」還是「少做了事」。

實作後先驗證行為等價：

```text
庫存 2、送 3 次    → 200 / 200 / 409，庫存 0、訂單 2   ✅
商品不存在         → 404                              ✅
相同 Key 送 3 次   → 庫存 9、訂單 1                    ✅
```

### 2.5 Measure Again

同一套 Profile、同一台機器、同一個壓測工具：

#### normal（100 VU、45 秒）

| | `Atomic` | `AtomicBatched` | 變化 |
|---|---:|---:|---|
| **RPS** | 135.0 | **199.3** | **+47.6%** |
| Avg | 619.7 ms | 419.7 ms | −32% |
| **P50** | 687.8 ms | **380.7 ms** | **−45%** |
| P95 | 813.7 ms | 682.4 ms | −16% |
| P99 | 926.1 ms | 734.1 ms | −21% |
| 錯誤率 | 0% | 0% | — |

#### stress（500 → 1000 → 5000 VU）

| | `Atomic` | `AtomicBatched` | 變化 |
|---|---:|---:|---|
| RPS | 187.3 | **210.1** | +12% |
| P50 | 15,002 ms | **13,116 ms** | −13% |
| **錯誤率** | **36.8%** | **22.5%** | **−39%** |

**結論：假設成立。**

縮短鎖的持有時間直接提高了吞吐量，而且沒有改變任何行為。
極限負載下的改善幅度較小（+12%）—— 因為那時瓶頸已經完全飽和，
但錯誤率從 36.8% 降到 22.5%，代表更多請求在逾時前被服務完成。

### 2.6 這次優化的代價

**EF Core 的 DB 命令計數器失明了。**

| | DbCommands（約 6000–9000 個請求） |
|---|---:|
| `Atomic` | 12,179 |
| `AtomicBatched` | **23** |

不是資料庫命令變少了 —— 是 `MetricsDbCommandInterceptor` 掛在 EF Core 的
攔截器管線上，而優化後的程式走原生 ADO.NET，**繞過了整條管線**。

這是繞過 ORM 的真實代價：**失去它附帶的一切**，
包括變更追蹤、查詢轉譯，以及這裡的觀測。

要補回來必須在 Repository 內手動計數，或改用 `DiagnosticsListener` 這類
更底層的機制。本階段未做，明確記錄在此。

**這正是「有數據依據的優化」應有的樣子：連代價也一起量出來。**

---

## 3. 回答計畫 §15 的十個問題

### 3.1 最初版本最大瓶頸是什麼？

**不是效能問題，是正確性問題。**

Stage 1 的 Baseline 在依序請求下完全正確（10 次請求 → 10 筆訂單、庫存 0）。
它的瓶頸是 `Read → Modify → Write` 不是原子操作 ——
單機低流量時看不出來，一併發就崩。

### 3.2 Race Condition 如何重現？

同一份程式碼、同一筆資料、庫存 10、送 100 個請求，
**唯一差別是併不併發**（[race-condition.md](load-test/race-condition.md)）：

```text
依序送出   Orders = 10    Stock = 0    Rejected = 90    ✅ 正確
併發送出   Orders = 100   Stock = 7    Rejected = 0     ❌ 超賣 90 件
```

`Rejected = 0` 最刺眼：系統**從頭到尾沒察覺庫存不足**，
100 個請求全部讀到 `Stock = 10` 並通過檢查。

比超賣更嚴重的是 **Lost Update**：100 筆訂單只扣掉 3 件庫存。
EF Core 產生的 `UPDATE` 是整個覆寫 `Stock` 欄位而非在資料庫端做減法，
最後一個寫入者贏，前面的全部消失。

### 3.3 最後採用什麼 Concurrency Control？

**Atomic Update**，Stage 10 再優化為單一往返版
（[concurrency-comparison.md](concurrency-comparison.md)）。

四種做法在秒殺情境（庫存 100、5000 請求）的比較：

| 方法 | Orders | 正確性 | RPS | P95 |
|---|---:|:---:|---:|---:|
| Baseline | 5000 | ❌ 超賣 4900 | 213 | 1212 ms |
| Transaction (UPDLOCK) | 100 | ✅ | 807 | 347 ms |
| Optimistic (rowversion) | 100 | ✅ | 489 | 2858 ms |
| **Atomic Update** | 100 | ✅ | **1122** | **250 ms** |

選它的理由不只是最快，還有**沒有失敗模式**：
另外兩者各有一個容易踩錯就失效的細節
（UPDLOCK 忘了加、重試後忘了卸離 Entity），Atomic 沒有。

樂觀鎖有一個必須記住的發現：庫存 5000、5000 個請求時，
它**只成交 896 筆、誤拒 4104 個庫存充足的請求**。
重試分布顯示衝突機率不隨重試次數衰減（第 1 次 5424、第 10 次 4466）——
**在持續高衝突下樂觀鎖不會收斂**，而秒殺正是它前提不成立的場景。

### 3.4 Redis 降低多少 DB Query？

讀取路徑 **5000 → 1**（[redis.md](load-test/redis.md)）：

| | DB 查詢數 | 命中率 | RPS | P99 |
|---|---:|---:|---:|---:|
| 快取關閉 | 5000 | — | 2,856 | 1,562 ms |
| 快取開啟 | **1** | 99.2% | **15,004** | **46 ms** |

是 1 而不是 200，因為 Single Flight 把 200 個同時發生的 Miss
收斂成一次查詢。

三個進階問題也實測了：

| | 無保護 | 有保護 |
|---|---:|---:|
| Cache Stampede（冷啟動 200 併發） | 77 | **1** |
| Cache Penetration（查不存在的 Id） | 5000（命中率 **0%**） | **200** |

**但 Redis 對寫入路徑毫無幫助。** 快取解決不了寫入。

### 3.5 Queue 對 Request Latency 有什麼影響？

**API 沒有變快，甚至略慢**（[queue.md](load-test/queue.md)）：

| | 同步 | 非同步 |
|---|---:|---:|
| API 回應完畢 | 45.1s | 48.6s |
| RPS | 112 | 104 |
| **資料庫命令數** | 10,003 | **5,013** |

發布訊息本身也要一次遠端往返，而瓶頸仍是庫存那一列。
我曾懷疑是每次發布都新建 Channel 造成，加上 Channel 池化後
重測 48.4s → 48.6s，**反證瓶頸不在該處**。

真正賺到的是**解耦**：Worker 設為每筆 100ms（慢 27 倍）時，
API 仍是 45.8s / 110 RPS，佇列峰值 4642，訂單需約 590 秒才全部落地 ——
**使用者完全無感**。這就是削峰填谷。

> Queue 的價值是解耦，不是加速。

### 3.6 Rate Limit 保護了什麼？

保護的是**系統在飽和時不會拖垮所有人**（[rate-limit.md](rate-limit.md)）。

| | 平均延遲 |
|---|---:|
| 正常處理 | 12.7 – 17.0 ms |
| **被 429 拒絕** | **0.1 ms** |

**拒絕成本是處理成本的 1/130。** 這是限流有效的前提 ——
限流器在 Controller 之前，被擋下的請求不碰資料庫或 Redis。

Stage 9 的數據解釋了為什麼必要：`stress/purchase` 的 **P50 就是 15 秒**，
最後 36.8% 逾時。**系統飽和時不會自己拒絕請求**，
它會讓所有人一起等到全部逾時。快速拒絕一部分人，
剩下的人才有機會被服務。

演算法選 SlidingWindow 而非 FixedWindow，因為後者有邊界爆發：
限制「20 次 / 10 秒」時，跨越視窗邊界的 3 秒內放行了 **39 個（1.95×）**，
而每個視窗看起來都完全合規。

### 3.7 Multi Instance 遇到了哪些問題？

**前七個階段留下的三處行程內狀態全部失準**
（[multi-instance.md](multi-instance.md)）：

| | 行程內 | Redis 共用 |
|---|---|---|
| 三台回報的 `DbCommands` | 36 / **0** / 93 | **3 / 3 / 3** |
| 冷快取造成的 DB 查詢 | 93 | **3** |
| 限流通過數（10/秒 × 3 秒） | **90（3 倍）** | **30** |

`api-2` 回報 `DbCommands=0` 最危險 —— 不是「沒有查詢」，
是「這台沒有查詢」，監控看起來一片祥和。

還有一個 Stage 7 就標記的陷阱：反向代理後面 `RemoteIpAddress`
會變成 Nginx 的 IP，per-IP 限流會把**所有人**算成同一個分區。

> 水平擴充只能擴充「不共享的東西」。
> 讀取加機器就能線性擴充，搶購加到三十台也還是那一列的鎖。

### 3.8 P95 / P99 如何變化？

**讀取路徑**（[baseline.md](load-test/baseline.md)）：

| VU | RPS | P95 | P99 |
|---:|---:|---:|---:|
| 10 | 7,509 | 2.0 ms | 6.0 ms |
| 100 | 26,570 | 5.1 ms | 10.4 ms |
| 5,000 | 31,151 | 96.9 ms | 204.3 ms |

100 → 5000 VU（50 倍負載），RPS 只多 17%，P99 漲 20 倍。
**這是飽和的典型形狀**：吞吐量到頂之後，多出來的負載全部變成排隊時間。

**搶購路徑**：

| VU | RPS | P50 | P99 | 錯誤率 |
|---:|---:|---:|---:|---:|
| 100 | 141 | 629 ms | 930 ms | 0% |
| 5,000 | 187 | 15,002 ms | 16,116 ms | 36.8% |
| 5,000（優化後） | **210** | **13,116 ms** | 15,688 ms | **22.5%** |

P50 就是 15 秒 —— 不是少數請求慢，是**一半以上的請求都在等**。

### 3.9 系統目前最大瓶頸在哪裡？

**仍然是 `Products` 表上那一列的排他鎖。**

Stage 10 的優化把臨界區從「橫跨三次網路往返」縮短到「僅伺服器端執行」，
吞吐量提升 47.6%，但**瓶頸的性質沒有改變** ——
所有請求依然要在同一列上排隊，只是排得快了一點。

證據依舊：優化後 CPU 峰值仍只有 **0.8~1.0%**。

### 3.10 如果流量再成長 10 倍，下一步會改什麼？

現在約 200 RPS，目標 2,000 RPS。依投資報酬率：

**1. 庫存扣減移到 Redis。** 用 Lua 腳本做原子扣減，資料庫只負責最終對帳。
Redis 處理單一 key 約 0.1 ms 量級，理論上限提升到數千 RPS。
Stage 8 的 `RedisSlidingWindowRateLimiter` 已證明這個模式可行。
代價是 Redis 成為真相來源，必須處理與資料庫的最終一致性與降級策略。

**2. 庫存分桶。** 100 件拆成 10 個桶各 10 件，鎖競爭從 1 列變 10 列。
代價是某桶賣完但其他桶還有貨時會誤判售完，需要跨桶重試。

**3. 純非同步搶購。** 連庫存扣減都放進佇列。資料庫端完全沒有競爭，
但使用者無法立即知道有沒有搶到 —— Stage 5 §2 已論證那是把超賣
從資料錯誤變成對客戶的謊言，只有業務上能接受「稍後通知」時才成立。

**不該做的**：加 API Instance、升級機器規格、加大連線池、
換併發控制演算法。CPU 才 1%，這些全部不會改變那一列的鎖。

---

## 4. 這個專案真正練到的東西

計畫 §21 說，完成後應該不是只會說「我會 Redis / RabbitMQ / Docker」，
而是能回答為什麼。逐題對照：

| 問題 | 本專案的答案 |
|---|---|
| 為什麼這裡需要 Redis？ | 讀取路徑 5000 次查詢 → 1 次。但寫入路徑毫無幫助 |
| 為什麼不能只使用 Transaction？ | READ COMMITTED 下共享鎖讀完就釋放，超賣照樣發生。關鍵是 UPDLOCK 不是 Transaction |
| 為什麼這個流程需要 Queue？ | 不是為了變快，是為了讓 API 的回應時間與訂單處理速度解耦 |
| 為什麼 API 必須 Stateless？ | 三處行程內狀態在多 Instance 下全部失準，其中一個還回報了會誤導人的 0 |
| 為什麼 P95 很低但 P99 很高？ | 飽和時多出來的負載全部變成排隊時間，長尾先炸 |
| 為什麼 Connection Pool 會耗盡？ | 5000 個 VU 各持一條連線在同一列上排隊，連無關的輕量查詢都擠不進去 |
| 為什麼 Retry 會產生 Duplicate Order？ | RabbitMQ 是 at-least-once；客戶端無法分辨「沒送到」與「成功但回應遺失」 |
| Rate Limit 應該放在哪一層？ | Controller 之前。拒絕成本必須遠低於處理成本（實測 1/130） |
| 這個 Bottleneck 是什麼？ | 單一資料列的鎖競爭。慢 167 倍的路徑 CPU 用得少 18 倍 |

### 過程中犯過而且值得記住的錯

工具本身出錯而導致結論錯誤，比程式有 bug 更危險 ——
因為它會安靜地給出看似合理的數字。

| 錯誤 | 後果 | 階段 |
|---|---|---|
| PowerShell 讀無 BOM 的 `.ps1` 用 ANSI codepage | 中文註解變亂碼破壞語法，統計欄位算出 `$null` | 2 |
| `@(Invoke-RestMethod ...).Count` | 陣列被當成單一物件，訂單數顯示 1（實際 5000） | 5 |
| k6 把限流分區鍵 `Number()` 後塞進 body | 非數字變 `null` 被驗證擋下回 400，誤計為「限流失效」 | 8 |
| `Start-Job` 當成並行爆發工具 | 每個要啟動新行程，60 個「並行」散布在數秒內跨越多個視窗 | 8 |
| 回報無權限查到的 DB 連線數 | 永遠是 1，會讓人以為連線池很閒而排除掉正確方向 | 9 |
| `Host.UseSerilog` 在 Serilog 10 已不取代預設 provider | 設定被靜默忽略，程式照跑但輸出仍是預設格式 | 10 |

---

## 5. 已知限制

誠實列出這份報告的邊界。

| 限制 | 影響 |
|---|---|
| k6 與 API 共用同一台機器的 12 核 | 讀取路徑 31k RPS 有一部分是 k6 造成的，適合比較版本不適合當絕對容量 |
| Docker Desktop 埠轉發約 100 req/s 就重設連線 | 無法對三 Instance 叢集做高流量壓測 |
| 沒有 SQL Server 的 `wait_stats` | 「瓶頸是那一列的鎖」是從應用程式端行為推論，證據充分但非直接證據 |
| DB 連線數量不到 | 需要 `VIEW SERVER STATE` 權限 |
| 優化後 EF 攔截器失明 | 繞過 ORM 的代價，DbCommands 指標不再反映真實命令數 |
| 訊息未傳遞 Trace Context | API 的 Producer Span 與 Worker 的 Consumer Span 不在同一個 Trace |
| Worker 尚未多 Instance | 理論上可直接加副本（競爭消費者 + 唯一索引去重），未實測 |
| Aspire Dashboard 資料在記憶體 | 重啟即消失，正式環境需要持久化後端 |

---

## 6. 重現步驟

```powershell
# 1. 儀表板
docker compose up -d aspire-dashboard      # http://localhost:18888

# 2. 啟動 API（壓測容量時關閉限流）
dotnet build -c Release
$env:ASPNETCORE_ENVIRONMENT="Development"
$env:RateLimit__Enabled="false"
dotnet run -c Release --no-build --project src/FlashSale.Api/FlashSale.Api.csproj --no-launch-profile --urls "http://localhost:5080"

# 3. 優化前後比較
.\tests\load\k6\Run-LoadTestSuite.ps1 -Profile normal -Scenario purchase -Strategy Atomic        -Label opt-before
.\tests\load\k6\Run-LoadTestSuite.ps1 -Profile normal -Scenario purchase -Strategy AtomicBatched -Label opt-after

# 4. 觀察
#    Traces / Metrics / Logs  -> http://localhost:18888
#    結構化日誌檔             -> src/FlashSale.Api/logs/flashsale-YYYYMMDD.jsonl
```
