# Stage 5 — Message Queue 量測紀錄

> Branch：`feature/message-queue`
> 日期：2026-08-28
>
> 前置：[Stage 3 併發控制比較](../concurrency-comparison.md)、[Stage 4 Redis](redis.md)

---

## 1. 這一階段要解決什麼

Stage 3 的結論是：三種併發控制都正確，但**吞吐量天花板一樣** ——
所有成交都要在資料庫的同一列上排隊，Atomic Update 也只有約 118 RPS。

Stage 4 用 Redis 把**讀取路徑**的資料庫壓力降到幾乎為零，
但**寫入路徑完全沒有改善** —— 快取解決不了寫入。

Stage 5 的問題是：能不能讓 API 不必等待訂單寫進資料庫就先回應？

---

## 2. 設計：哪一段可以非同步，哪一段不行

```text
POST /api/flash-sale/{id}
        │
        ▼
  UPDATE Stock = Stock - qty          ← 同步。這一步決定「有沒有買到」
  WHERE Id = @id AND Stock >= qty
        │
        ▼
  Publish OrderCreated                ← 訊息進佇列
        │
        ▼
  202 Accepted                        ← 立即回應，訂單尚未建立
```

**扣庫存不能非同步。** 它是唯一決定「這個人有沒有買到」的判斷。
放進佇列的話，API 在還不知道有沒有庫存時就得先回應「成功」，
之後才發現賣完 —— 那是把超賣從資料錯誤變成了對客戶的謊言。

**建訂單可以非同步。** 庫存已經扣掉，這筆交易一定成立，
訂單什麼時候寫進資料庫不影響正確性。

> 削峰填谷只能套用在**可以容忍延遲**的工作上。

回應也因此改變語意：同步路徑回 `200 OK` 帶訂單，
非同步路徑回 `202 Accepted` 帶追蹤碼、`order` 欄位為 `null`。
用 `Id = 0` 的假訂單來表達「還沒建立」會讓客戶端無法分辨
「尚未建立」與「建立失敗」。

---

## 3. 拓撲

```text
                    ┌──────────────────────────┐
    API ──publish──▶│ flashsale.orders         │──▶ flashsale.orders.created
                    │ (direct exchange)        │         │
                    └──────────────────────────┘         │ Worker 消費
                                 ▲                       │
                 TTL 到期後      │                       ├── 成功 ──▶ ACK
                 由 DLX 自動送回 │                       │
                    ┌────────────┴─────────────┐         ├── 可重試失敗
                    │ flashsale.orders.retry   │◀────────┘   （重新發布到重試佇列）
                    │ x-message-ttl: 5000      │
                    │ x-dead-letter-exchange   │
                    └──────────────────────────┘
                                                          └── 無法解析 / 重試用盡
                    ┌──────────────────────────┐              │
                    │ flashsale.orders.dlx     │◀─────────────┘
                    └──────────────────────────┘
                                 │
                                 ▼
                    flashsale.orders.created.dlq   （人工排查，不自動丟棄）
```

### 為什麼重試不用 `BasicNack(requeue: true)`

那會讓失敗訊息**立刻**回到佇列頭部被重新取出，形成沒有間隔的忙碌迴圈。
資料庫正在掛掉的時候，這只會讓它掛得更徹底。

改成把訊息重新發布到一個帶 `x-message-ttl` 的重試佇列，
TTL 到期後由 Dead Letter 機制自動送回主佇列 —— 等待期間 Consumer
可以繼續處理其他訊息。重試次數記在 Header（`x-flashsale-retry-count`），
因為那是傳輸層的關注點，不屬於商業資料。

### 兩種失敗，處理方式刻意不同

| 失敗類型 | 處理 | 理由 |
|---|---|---|
| 訊息無法解析（毒訊息） | **直接進 DLQ，不重試** | 無法解析的內容重試一百次也不會突然變得可以解析 |
| 暫時性錯誤（DB 斷線、逾時） | 重試 3 次後才進 DLQ | 有機會在稍後成功 |

DLQ 刻意**不設 TTL**：自動丟棄失敗訂單等於靜默地失去客戶的錢。

---

## 4. 測試環境

| 項目 | 內容 |
|---|---|
| API | ASP.NET Core 9，Release，單一 Instance |
| Worker | .NET 9 Worker Service，單一 Instance，`PrefetchCount = 1` |
| 資料庫 | SQL Server（遠端主機） |
| Broker | RabbitMQ 3.13.7（遠端主機），AMQP 5672 |
| 壓測 | k6 v2.2.0，`shared-iterations`，5000 次請求 / 200 連線 |

```powershell
.\tests\load\k6\Run-QueueTest.ps1 -Strategy Atomic       -Stock 5000 -Iterations 5000
.\tests\load\k6\Run-QueueTest.ps1 -Strategy AtomicQueued -Stock 5000 -Iterations 5000
```

---

## 5. 主要結果：同步 vs 非同步

庫存 5000、5000 個請求，全部都能成交（工作量一致才能比較）：

| | 同步 `Atomic` | 非同步 `AtomicQueued` |
|---|---:|---:|
| API 回應完畢 | **45.1s** | 48.6s |
| API RPS | **112** | 104 |
| Avg | 1755 ms | 1881 ms |
| P95 | 2158 ms | 2169 ms |
| P99 | 4692 ms | **3067 ms** |
| **資料庫命令數** | 10003 | **5013** |
| 訂單全部落地 | 45.2s | 69.8s |
| 佇列峰值 | 0 | 2470 |

### 這個結果和直覺不一樣

**API 沒有變快，甚至略慢。**

原本的預期是：同步版本把 UPDATE 與 INSERT 包在同一個交易裡，
那一列的排他鎖要持有到 INSERT 完成才釋放；非同步版本的 UPDATE
是獨立交易，鎖只在單一語句期間持有，臨界區更短，應該更快。

實測不是這樣。原因：

1. **發布訊息本身要一次遠端往返。** Broker 與資料庫在同一台遠端主機上，
   「publish + 等待 Publisher Confirm」的成本和「INSERT 一筆訂單」差不多。
   等於把一次資料庫寫入換成一次 Broker 往返，沒有淨賺。

2. **瓶頸仍然是那一列。** 扣庫存還是同步的，5000 個請求仍然要在
   `Products` 的同一列上排隊。這一段沒有被改變，總時間就不會有數量級的差別。

我一度懷疑是每次發布都新建 Channel（開啟 Channel 需要一次 AMQP 往返）
造成的額外成本，因此加上了
[Channel 池化](../../src/FlashSale.Api/Infrastructure/Messaging/ChannelPool.cs)
並重新量測 —— **48.4s → 48.6s，沒有差別**。
這反過來證實了瓶頸不在 Channel 建立，而在上面兩點。

### 那到底賺到了什麼

| 賺到 | 沒賺到 |
|---|---|
| **資料庫寫入量減半**（10003 → 5013） | API 吞吐量 |
| P99 從 4692ms 降到 3067ms（少了長尾） | Avg / P95 |
| API 回應時間與訂單處理速度**完全解耦**（見 §6） | |
| Worker 可獨立擴充、重啟、停機 | |

資料庫是整個系統中最難水平擴充的資源。把它的寫入量砍半，
即使 API 吞吐量沒變，系統的可擴充性也改善了。

**結論：Queue 的價值是解耦，不是加速。**
如果同步的那一段仍然是瓶頸，佇列不會讓 API 變快。

---

## 6. 削峰填谷：Consumer 變慢時會發生什麼

計畫 §10 指定的測試：Consumer 每筆處理 100 ms，突然湧入 5000 個請求。

```powershell
$env:Worker__SimulatedProcessingMs = "100"
```

### 結果

| | 正常 Worker | 慢速 Worker（100ms/筆） |
|---|---:|---:|
| API 回應完畢 | 48.6s | **45.8s** |
| API RPS | 104 | **110** |
| API P95 | 2169 ms | **2084 ms** |
| 佇列峰值 | 2470 | **4642** |
| Worker 處理速率 | ~230 筆/秒 | **8.47 筆/秒** |
| 訂單全部落地（推估） | 69.8s | **~590s** |

**Worker 慢了 27 倍，API 的表現完全沒有改變。**

佇列長度曲線（節錄）：

```text
  t=  45.8s  佇列=4642  訂單= 357     ← API 已全部回應完畢
  t=  60.0s  佇列=4524  訂單= 475
  t= 100.7s  佇列=4179  訂單= 820
  t= 150.0s  佇列=3762  訂單=1237
  t= 197.1s  佇列=3361  訂單=1638
                ↓ 以 8.47 筆/秒持續下降
  推估 t≈590s  佇列=   0  訂單=5000
```

這就是「削峰填谷」：

- **峰**：45.8 秒內湧入 5000 筆待處理工作，佇列吸收了它
- **谷**：之後由 Worker 以自己能負荷的固定速度慢慢消化
- 兩端的速度差了 **13 倍**，而使用者完全感受不到

> API 不一定要跟後端工作以相同速度處理。

如果沒有佇列，這 5000 個請求會直接壓在資料庫上；
Worker 的處理速度就會變成 API 的回應速度。

---

## 7. ACK / Retry / DLQ 的實測

### 7.1 訊息不會因為沒有 Consumer 而遺失

Worker 未啟動時送出 5 筆非同步搶購：

```text
API 回應         5 × HTTP 202
庫存             50 → 45      ← 已同步扣減
訂單數           0            ← Worker 未啟動
佇列 pendingOrders  5
```

啟動 Worker 後：

```text
佇列 pendingOrders  0
訂單數           5
庫存             45           ← 與訂單數一致
```

佇列與訊息都設為 `durable` / `persistent`，
Broker 重啟後訊息仍在。兩者只做其中一個都會遺失訊息。

### 7.2 毒訊息直接進 DLQ

透過管理 API 發布一段不是合法 JSON 的內容：

```powershell
.\tests\load\k6\Send-PoisonMessage.ps1 -ManagementUrl "http://<host>:15672" -User <u> -Password <p>
```

```text
Worker log : Poison message, sending straight to DLQ. DeliveryTag=6
佇列       : pendingOrders=0  pendingRetries=0  deadLettered=1
```

**沒有經過重試** —— 這是刻意的。

### 7.3 暫時性錯誤會重試

讓 Worker 連到不存在的資料庫（用環境變數覆寫連線字串，
這正是「資料庫掛了」這種該重試的狀況）：

```text
Worker log:
    2 × scheduling retry 1/3
    2 × scheduling retry 2/3
    2 × scheduling retry 3/3
    2 × Retry limit reached

佇列變化:
    pendingOrders=2 → pendingRetries=2 → ... → deadLettered +2
```

兩則訊息各重試 3 次後進入 DLQ，全程約 66 秒
（每輪 = 資料庫連線逾時 + 5 秒 TTL）。

---

## 8. 已知問題：這一階段引入了重複訂單的可能

RabbitMQ 保證的是 **at-least-once**。以下情況都會讓同一則訊息被消費兩次：

- Worker 在建立訂單後、`BasicAck` 之前崩潰
- 重試機制把一則其實已經成功（只是回應遺失）的訊息重新投遞
- 網路層的重送

目前 `OrderCreatedConsumer` **沒有做去重**，因此會產生重複訂單。

這是刻意留給 **Stage 6 (Idempotency)** 的問題 ——
`OrderCreatedMessage.MessageId` 已經帶在訊息裡，就是為了那一階段用。

### 另一個殘餘風險：Publish 失敗的補償

`QueuedAtomicFlashSalePurchaseStrategy` 在「庫存已扣減但訊息發布失敗」時
會把庫存加回去。但如果訊息其實**已經送達**、只是「確認」在回程遺失，
補償就會把庫存加回去、訂單卻仍然會建立 —— 庫存與訂單不一致。

要徹底解決需要 **Transactional Outbox**：把訊息與庫存變更寫在同一個
資料庫交易裡，再由背景程序負責投遞。那超出本階段範圍，此處僅記錄。

---

## 9. 過程中修正的問題

### 9.1 Worker 讀不到設定檔

Worker 的 `appsettings` 是從 `FlashSale.Api` 連結過來的，只存在於建置輸出目錄。
但 `dotnet run` 預設把**專案目錄**當成 content root，那裡沒有這些檔案。

結果所有設定都是空的，`RabbitMq:HostName` 變成空字串，
`ConnectionFactory` 沒有報錯而是嘗試連到一個解析出來的 link-local 位址
（`169.254.83.107:5672`）—— 錯誤訊息裡只看得到那個位址，
完全看不出真正的原因是設定沒載入。

修正：
- `Host.CreateApplicationBuilder` 明確指定 `ContentRootPath = AppContext.BaseDirectory`
- `RabbitMqConnectionProvider` 在 `HostName` 為空時直接丟出說明清楚的例外

### 9.2 BackgroundService 不能注入 Scoped 服務

`OrderCreatedConsumer` 是 `BackgroundService`（Singleton），
原本注入 Scoped 的 `IMessagePublisher`，啟動時直接失敗。

`IMessagePublisher` 與 `IQueueInspector` 都是無狀態的（每次呼叫自己借 Channel），
本來就應該註冊為 Singleton。

而每則訊息內部**必須**自己開一個 Scope 才能取得 `IOrderRepository` ——
否則整個 Worker 生命週期共用一個 `DbContext`，變更追蹤會無限累積。

### 9.3 PowerShell 把陣列當成單一物件

```powershell
# 錯：得到 1
$count = @(Invoke-RestMethod -Uri "...").Count

# 對：得到實際元素個數
$list  = Invoke-RestMethod -Uri "..."
$count = @($list).Count
```

`Invoke-RestMethod` 回傳陣列時是以**單一物件**寫入管線，
`@()` 會把整個陣列包成一個元素。**不會報錯，只會安靜地給出錯誤的數字** ——
第一次量測時 `OrdersFinal` 顯示 1（實際是 5000），差點得出錯誤結論。

---

## 10. 重現步驟

```powershell
# 1. 準備 RabbitMQ（若沒有現成的）
docker compose up -d rabbitmq
#    然後把 appsettings.Development.json 的 RabbitMq 區段指向 localhost:5672

# 2. 建置（API 執行中會鎖住檔案，Worker 建置時會連帶重建 API）
dotnet build -c Release

# 3. 啟動 API
$env:ASPNETCORE_ENVIRONMENT="Development"
dotnet run -c Release --no-build --project src/FlashSale.Api/FlashSale.Api.csproj --no-launch-profile --urls "http://localhost:5080"

# 4. 啟動 Worker（另一個終端機）
$env:DOTNET_ENVIRONMENT="Development"
dotnet run -c Release --no-build --project src/FlashSale.Worker/FlashSale.Worker.csproj --no-launch-profile

# 5. 比較同步 / 非同步
.\tests\load\k6\Run-QueueTest.ps1 -Strategy Atomic       -Stock 5000 -Iterations 5000
.\tests\load\k6\Run-QueueTest.ps1 -Strategy AtomicQueued -Stock 5000 -Iterations 5000

# 6. 削峰填谷（Worker 設為每筆 100ms 後重啟）
$env:Worker__SimulatedProcessingMs="100"
.\tests\load\k6\Run-QueueTest.ps1 -Strategy AtomicQueued -Stock 5000 -Iterations 5000 -DrainTimeoutSeconds 150
```

佇列長度取樣會寫入 `tests/load/k6/results/*-samples.csv`（已 gitignore）。
