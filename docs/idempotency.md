# Stage 6 — Idempotency

> Branch：`feature/idempotency`
> 日期：2026-09-01
>
> 前置：[Stage 5 Message Queue](load-test/queue.md)

---

## 1. 要解決的問題

計畫 §11 的一句話：

> **Retry != 執行兩次**

具體情境：

```text
Client                          Server
  │                               │
  ├── Request #1 ────────────────▶│
  │                               ├── 扣庫存、建立訂單 ✅
  │         ✗ Response Timeout ───┤
  │                               │
  ├── Request #2（重試）─────────▶│
  │                               └── ？？？
```

客戶端**無法分辨**「請求沒送到」與「請求成功了但回應遺失」。
它唯一能做的就是重試。系統必須讓這次重試不要產生第二筆訂單。

Stage 5 還留下了第二個來源：RabbitMQ 是 **at-least-once**，
Worker 在 ACK 前崩潰、或重試機制重投一則其實已成功的訊息，
同樣會產生重複訂單。

兩者要一起解決。

---

## 2. 三層防護

```text
┌─────────────────────────────────────────────────────────────┐
│ 第一層：IdempotencyFilter（HTTP 邊界）                       │
│   以 Idempotency-Key 原子佔用 → 重送直接回放原本的回應       │
│   → 業務邏輯完全不執行                                       │
└─────────────────────────────────────────────────────────────┘
                            │ 失效時（Redis 故障 / 設定關閉）
                            ▼
┌─────────────────────────────────────────────────────────────┐
│ 第二層：Order.IdempotencyKey 篩選唯一索引（資料庫）          │
│   同一個 Key 不可能有兩筆訂單 → 違反時 Rollback 並回 409     │
└─────────────────────────────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────┐
│ 第三層：Worker 消費端去重（同一個唯一索引）                  │
│   重複投遞的訊息 → TryCreateAsync 回 false → 視為成功並 ACK  │
└─────────────────────────────────────────────────────────────┘
```

**為什麼要三層。** 第一層最好（客戶端拿到跟第一次一樣的答案），
但它依賴 Redis；Redis 會掛。第二層由資料庫強制執行，不依賴任何外部元件，
但它只能拒絕、無法回放。第三層處理的是 Broker 重送 ——
那條路徑根本不經過 HTTP，第一層看不到它。

---

## 3. 第一層：IdempotencyFilter

### 流程

```text
收到請求
  │
  ▼
沒帶 Idempotency-Key ──▶ 照常執行（不受保護；Required=true 時回 400）
  │
  ▼
原子佔用這個 Key
  │
  ├── 佔用成功 ──▶ 執行 ──▶ 保存回應 ──▶ 回傳
  │                  └─ 拋例外 ──▶ 釋放佔用
  │
  ├── 狀態 = Completed  ──▶ 回放先前保存的回應（不再執行）
  │
  └── 狀態 = InProgress ──▶ 409（有另一個相同請求正在處理）
```

### 三個關鍵設計

**佔用必須是原子的。** 「先查有沒有、再建立記錄」在併發下會讓兩個請求
同時通過查詢。Redis 用 `SET NX`，SQL Server 用主鍵衝突 ——
兩者都是把判斷交給儲存層的原子操作。

**`InProgress` 狀態存在的唯一理由是併發重複。** 少了它，
兩個同時抵達的請求都會看到「查無此 Key」而各自建立訂單。
第二個請求收到 409「處理中」，而不是等待 —— 讓客戶端稍後重試，
不要佔住連線。

**失敗時必須釋放佔用。** 否則這個 Key 會卡在 `InProgress` 直到 TTL 到期，
使用者重試只會一直收到「處理中」而無法真正重試。

同時**刻意不保存失敗的回應**：庫存不足是 409，但那可能只是這一瞬間的狀態，
補貨後同一個 Key 應該能重新嘗試。

### 為什麼放在 Filter 而不是 Service

「同一個請求被送了兩次」是 HTTP 傳輸層的問題，不是商業規則。
而且要回放的是**完整的 HTTP 回應**（狀態碼 + 內容），
那是 Controller 邊界才有的東西。

---

## 4. 儲存體比較（計畫 §11 要求）

兩個實作並存，由 `Idempotency:Provider` 決定，兩者都通過全部測試。

| | Redis | SQL Server |
|---|---|---|
| 原子性來源 | `SET NX` | 主鍵衝突 |
| 過期處理 | **自動**（`EX` 參數） | **需自行判斷 + 另外清理** |
| 額外負載 | 落在 Redis | 落在**已經是瓶頸的資料庫** |
| 故障影響 | 保護失效，退回第二層 | 資料庫掛了整個系統本來就停擺 |
| 持久性 | 取決於 Redis 設定 | 與訂單同一個資料庫，一致 |

### 取捨

**Redis 是預設。** 秒殺場景下資料庫是最稀缺的資源，
Stage 3 已經證明所有成交都要在庫存那一列上排隊。
把冪等檢查也丟給資料庫，等於在最擁擠的地方再加一次往返。

**但 Redis 版有一個 SQL Server 版沒有的弱點**：Redis 掛掉時保護會失效，
此時只剩第二層的資料庫唯一索引 —— 它能防止重複訂單，
但客戶端收到的是 409 而不是原本的成功回應。

SQL Server 版還有一個必須處理的問題：**沒有自動過期**。
`ExpiresAt` 要自己判斷，過期記錄還需要另外的清理機制
（見 §8 已知限制）。Redis 的 TTL 直接省掉這整件事。

---

## 5. 第二層：資料庫唯一索引

```sql
CREATE UNIQUE INDEX IX_Orders_IdempotencyKey
ON Orders (IdempotencyKey)
WHERE IdempotencyKey IS NOT NULL;
```

**必須加 `WHERE ... IS NOT NULL`。** SQL Server 的唯一索引把多個 NULL
視為互相衝突，不加篩選的話「沒帶 Key 的訂單」只能存在一筆。

`Order.IdempotencyKey` 的來源：

| 路徑 | 來源 |
|---|---|
| 同步 | 客戶端的 `Idempotency-Key` header |
| 非同步 | 訊息的 `IdempotencyKey`（= 客戶端的 Key，沒帶時退回 MessageId） |

非同步路徑刻意**不直接用 MessageId**：客戶端重送時 API 會產生**新的**
MessageId，兩則訊息會被視為不同的訂單。只有客戶端的 Key 才能
讓 Worker 認出「這是同一筆訂單」。

觸發時的處理：`TryCreateAsync` 回傳 false → **Rollback**（庫存在建單前
就扣掉了，這次沒有真的賣出東西）→ 丟出 `BusinessException` → 409。

---

## 6. 驗證結果

```powershell
.\tests\load\k6\Run-IdempotencyTest.ps1 -Strategy Atomic
.\tests\load\k6\Run-IdempotencyTest.ps1 -Strategy AtomicQueued
```

### 6.1 Retry Test（依序重送 5 次，相同 Key）

| 策略 | #1 | #2–#5 | 訂單數 | 庫存 | 結果 |
|---|---|---|---:|---:|:---:|
| `Atomic` | 200 | **200（回放）** | 1 | 99 | ✅ |
| `AtomicQueued` | 202 | **202（回放）** | 1 | 99 | ✅ |

回放的回應帶 `Idempotency-Replayed: true` 標頭，
內容與第一次**完全相同**。

> 冪等不只是「不重複執行」，還要「拿到跟第一次一樣的答案」。
> 客戶端本來就是因為沒收到答案才重試的。

### 6.2 Concurrent Duplicate Test（50 個同時請求，相同 Key）

這是最嚴苛的情況：沒有任何時間差讓第一個請求先完成並寫下記錄。

| 策略 | 受理 2xx | 409 處理中 | 其他錯誤 | 訂單數 | 庫存 | 結果 |
|---|---:|---:|---:|---:|---:|:---:|
| `Atomic` | 1 | 49 | 0 | 1 | 99 | ✅ |
| `AtomicQueued` | 1 | 49 | 0 | 1 | 99 | ✅ |

兩種 Provider（Redis / SqlServer）結果相同。

### 6.3 Worker 去重（繞過 API，直接重複投遞）

用 RabbitMQ 管理 API 把**同一個 MessageId** 的訊息重複投遞 3 次：

```text
#1 routed=True
#2 routed=True
#3 routed=True

訂單數 : 1        ← 唯一索引擋下了 3 次重複
DLQ    : 沒有增加  ← 正確地視為成功並 ACK
```

Worker log：

```text
Duplicate message ignored, order already exists. MessageId=5cb13de3-...
Duplicate message ignored, order already exists. MessageId=5cb13de3-...
Duplicate message ignored, order already exists. MessageId=5cb13de3-...
```

**重複投遞不是錯誤。** 如果當成失敗，這則訊息會不斷重試，
最後進 DLQ —— 但訂單其實早就建好了，人工排查只會白費工夫。

---

## 7. 關閉第一層會怎樣（證明它有作用）

```powershell
$env:Idempotency__Enabled = "false"
```

| | Filter 開啟 | Filter 關閉 |
|---|---|---|
| 重送 #2–#5 | **200 + 原本的回應** | **409** |
| 併發 50 個 | 1 受理 / 49 個 409 | 1 受理 / 49 個 409 |
| **訂單數** | **1** | **1** |
| **庫存** | **99** | **99** |

**資料不會錯 —— 第二層守住了。** 差別在使用者體驗：
關閉時客戶端拿不到原本的成功回應，只知道「重複了」，
無法得知訂單編號。

### 過程中修正的缺陷

第一次量測時，關閉 Filter 的重送回傳的是 **500**，不是 409。

原因：策略當時用的是 `CreateAsync`，唯一索引違反直接變成
未處理的 `DbUpdateException`。但**唯一索引衝突是可預期的已知狀況**，
不該當成系統錯誤。

已改為 `TryCreateAsync` → Rollback → `BusinessException` → 409。
有測試釘住「觸發時必須 Rollback」——
少了那個 Rollback，每一次重複請求都會憑空少掉一件庫存。

---

## 8. 已知限制

### SQL Server 版的記錄不會自己消失

`IdempotencyRecords` 表沒有自動清理機制。TTL 預設 24 小時，
過期記錄會一直累積。正式環境需要排程作業定期刪除
（`ExpiresAt` 上已建索引，避免全表掃描）。

Redis 版沒有這個問題 —— `EX` 到期就消失。

### 非同步路徑的殘餘不一致

Filter 關閉時，`AtomicQueued` 的重送會：

- 庫存被扣兩次（扣庫存發生在任何去重點之前）
- 但只建立一筆訂單（唯一索引擋下第二筆）

也就是說 **`AtomicQueued` 沒有第一層就無法完全冪等**。
根本原因是庫存扣減必須同步（Stage 5 §2 已說明為何不能非同步），
而它發生在訊息與訂單之前。

要徹底解決需要 Transactional Outbox（庫存變更與訊息寫在同一個
資料庫交易），與 Stage 5 記錄的殘餘風險是同一個問題。

### Idempotency-Key 沒有綁定請求內容

目前只檢查 Key 是否重複，不檢查「這次請求的內容是否與第一次相同」。
惡意或有 bug 的客戶端用同一個 Key 送不同的 body，
會拿到第一次的回應。

正式做法是把 request body 的雜湊一起存進記錄，不符時回 422。
本階段未實作。

---

## 9. 重現步驟

```powershell
# 1. 套用 migration（新增 IdempotencyRecords 表與 Orders.IdempotencyKey 索引）
$env:ASPNETCORE_ENVIRONMENT="Development"
dotnet ef database update --project src/FlashSale.Api/FlashSale.Api.csproj

# 2. 建置並啟動 API 與 Worker
dotnet build -c Release
dotnet run -c Release --no-build --project src/FlashSale.Api/FlashSale.Api.csproj --no-launch-profile --urls "http://localhost:5080"
dotnet run -c Release --no-build --project src/FlashSale.Worker/FlashSale.Worker.csproj --no-launch-profile

# 3. 驗證
.\tests\load\k6\Run-IdempotencyTest.ps1 -Strategy Atomic
.\tests\load\k6\Run-IdempotencyTest.ps1 -Strategy AtomicQueued

# 4. 切換儲存體
$env:Idempotency__Provider="SqlServer"   # 重啟 API 後再跑一次

# 5. 關閉第一層，觀察差異
$env:Idempotency__Enabled="false"        # 重啟 API 後再跑一次
```

手動測試：

```bash
KEY=$(uuidgen)
curl -X POST http://localhost:5080/api/flash-sale/1 \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: $KEY" \
  -d '{"userId":1,"quantity":1}'

# 再送一次，會拿到完全相同的回應 + Idempotency-Replayed: true
curl -i -X POST http://localhost:5080/api/flash-sale/1 \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: $KEY" \
  -d '{"userId":1,"quantity":1}'
```
