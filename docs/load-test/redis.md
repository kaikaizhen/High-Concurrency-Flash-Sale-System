# Stage 4 — Redis 快取量測紀錄

> Branch：`feature/redis`
> 日期：2026-08-28
>
> 前置：[Stage 3 併發控制比較](../concurrency-comparison.md)

---

## 1. 先釐清一件事：Redis 救不了搶購的寫入路徑

計畫 §9 的原則是「Cache 的目的不是『用了 Redis』，而是減少昂貴資源的存取」。
照這個原則，第一個該問的問題是：**哪一條路徑的資料庫存取最昂貴且最可快取？**

Stage 3 選定的 Atomic Update 在成功路徑上**完全不讀取商品**，它只送一個
`UPDATE ... WHERE Id = @id AND Stock >= @qty`。所以：

- 替商品加快取，對搶購的**寫入路徑毫無幫助**。
- 寫入本來就不能用快取解決 —— 那是 Stage 5（Queue）的題目。

真正該快取的是**讀取路徑**：秒殺開始前所有人都在刷新商品頁。
讀取量比寫入量高出好幾個數量級，而且同一筆商品被反覆讀取 —— 這才是快取發揮作用的地方。

因此本階段壓測的是 `GET /api/products/{id}`。

---

## 2. 測試環境與方法

| 項目 | 內容 |
|---|---|
| API | ASP.NET Core 9，**Release** 組態，單一 Instance |
| 資料庫 | SQL Server（遠端主機），EF Core 9 |
| 快取 | Redis 7（遠端主機），StackExchange.Redis |
| 壓測工具 | k6 v2.2.0，`shared-iterations` |
| 負載 | 5000 次請求，200 個同時連線 |

### 資料庫查詢次數怎麼算出來的

不是估算，也不是讀 log。
[`MetricsDbCommandInterceptor`](../../src/FlashSale.Api/Data/Interceptors/MetricsDbCommandInterceptor.cs)
掛在 EF Core 的攔截器管線上，每一個**實際送到資料庫的命令**都會加一。
快取命中／未命中則由 `RedisCacheService` 累加。

壓測腳本在每一輪開始前呼叫 `POST /api/diagnostics/metrics/reset`，
結束後讀 `GET /api/diagnostics/metrics`。

```powershell
.\tests\load\k6\Run-CacheTest.ps1 -Label "cache-on"
```

Before / After 的切換靠啟動 API 時的環境變數：

```powershell
$env:Cache__Enabled = "false"   # Before
$env:Cache__Enabled = "true"    # After
```

---

## 3. 主要結果：Before / After

5000 次請求打同一筆商品：

| | DB Queries | Cache Hits | Cache Misses | 命中率 | 總耗時 | RPS | Avg | P95 | P99 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| **快取關閉** | **5000** | 0 | 0 | — | 4.6s | 2856 | 67 ms | 460 ms | 1562 ms |
| **快取開啟** | **1** | 4999 | 42 | 99.2% | 0.7s | **15004** | **12 ms** | **26 ms** | **46 ms** |

- 資料庫查詢：**5000 → 1**，減少 99.98%
- RPS：**2856 → 15004**，5.3 倍
- P99：**1562 ms → 46 ms**，降到 3%

「5000 → 1」而不是「5000 → 200」，是因為 Single Flight 把 200 個同時發生的
Miss 收斂成一次資料庫查詢。沒有這層保護會是 §5.1 的結果。

> 注意 Cache Misses (42) + Cache Hits (4999) > 5000。
> Single Flight 的路徑上會讀兩次快取（取鎖前一次、取鎖後 double-check 一次），
> 所以總讀取次數會超過請求數。

---

## 4. Cache Aside 的實作

流程寫在
[`ProductService.GetByIdAsync`](../../src/FlashSale.Api/Services/ProductService.cs)：

```text
讀快取
  ├── Hit  → 直接回傳
  └── Miss → 取得 Key 層級的鎖
              → 再讀一次快取（double-check）
                  ├── Hit  → 回傳（前面的請求已經填好了）
                  └── Miss → 查資料庫 → 寫回快取 → 回傳
```

### 三個容易寫錯的細節

**1. 取得鎖之後必須再讀一次快取。**
少了這次 double-check，排隊的請求還是會一個個去查資料庫，
只是從「併發查 200 次」變成「串行查 200 次」—— 查詢次數並沒有減少，
還多付了排隊的延遲。有測試釘住這件事
（`GetByIdAsync_WhenSingleFlightEnabled_ShouldRecheckCacheAfterAcquiringLock`）。

**2. 快取的是 DtoModel，不是 Entity。**
Entity 帶有 `RowVersion` 這種 EF 追蹤用的欄位，序列化進快取沒有意義，
反序列化回來的物件也不在 DbContext 的追蹤範圍內，當成 Entity 使用會出錯。

**3. 先寫資料庫，再清快取。**
反過來（先清快取再寫資料庫）中間有一個空窗：清完之後、寫入之前，
其他請求會把**舊值**重新載入快取，於是舊值又活了一個完整的 TTL。

---

## 5. 三個進階問題的實測

### 5.1 Cache Stampede / Breakdown

**問題**：快取失效或冷啟動的瞬間，N 個併發請求同時 Miss，
於是 N 個請求同時去查資料庫。快取原本要保護的資料庫，
反而在最脆弱的時刻承受最集中的衝擊。

**實測**（5000 請求 / 200 連線，冷快取）：

| Single Flight | DB Queries | 命中率 | P95 |
|---|---:|---:|---:|
| 關閉 | **77** | 98.5% | 56 ms |
| 開啟 | **1** | 99.2% | 26 ms |

77 倍的差距。而且這是本機測試、資料庫回應很快的情況 ——
資料庫越慢，冷啟動的併發窗口越長，同時 Miss 的請求就越多，差距會更大。

**解法**：Key 層級的互斥鎖，同一個 Key 只讓一個請求去查資料庫，其餘等它的結果。
實作在 [`KeyedLock`](../../src/FlashSale.Api/Infrastructure/Cache/KeyedLock.cs)。

### 5.2 Cache Penetration

**問題**：查詢**不存在**的資料。快取永遠不會有這個 Key，
所以每一次請求都會穿透快取直達資料庫。快取形同不存在。

這是可以被惡意利用的 —— 攻擊者只要不斷請求隨機的不存在 Id，
就能讓所有流量直接打到資料庫。

**實測**（5000 次請求打 `/api/products/999999`）：

| 負向快取 | DB Queries | Cache Hits | 命中率 | RPS |
|---|---:|---:|---:|---:|
| 關閉 | **5000** | 0 | **0%** | 2607 |
| 開啟 | **200** | 4800 | 96.0% | 6505 |

關閉時命中率是 **0%** —— 快取提供了**完全零**的保護。

**解法**：把「查無此資料」本身也快取起來（負向快取）。

實作上必須能區分「Key 不存在」與「Key 存在但值是 null」，
否則快取起來的 null 每次都會被當成 Miss。
[`CacheResult<T>`](../../src/FlashSale.Api/Infrastructure/Cache/ICacheService.cs)
用 `Found` 旗標把兩者分開，Redis 裡則存一個哨兵字串。

**代價**：負向快取的 TTL 必須遠短於正常 TTL（預設 3 秒 vs 10 秒）。
否則之後真的建立了這個 Id 的商品，會有一段時間查不到。
`ProductService.CreateAsync` 也會主動清除該 Id 的快取，就是為了這個。

### 5.3 為什麼沒有做隨機化 TTL

Cache Avalanche（大量 Key 同時到期）的常見解法是給 TTL 加隨機抖動。
本專案只快取單一商品的 Key，秒殺場景下熱點高度集中在少數幾個 Key 上，
不存在「大量 Key 同時到期」的問題 —— 那是 Single Flight 已經處理的
Breakdown（單一熱點 Key 到期），不是 Avalanche。

若未來快取範圍擴大到整份商品清單或大量商品，就需要重新評估。

---

## 6. 過程中發現並修正的問題

功能驗證時發現一條寫入路徑會改動被快取的資料，卻沒有讓快取失效：

```text
搶購前(快取)  stock = 50
搶購 5 次
搶購後(快取)  stock = 50    ← 錯誤，真實庫存是 45
```

`ProductService.UpdateAsync` 有做快取失效，但**搶購走的是 `FlashSaleService`**，
它同樣會改動庫存卻沒有清快取。結果是商品頁在**整場秒殺期間**都顯示錯誤的庫存 ——
偏偏那是最多人在看的時候。

已在 `FlashSaleService.PurchaseAsync` 成交後補上失效：

```text
搶購前(快取)  stock = 50
搶購 5 次
搶購後        stock = 45    ← 正確
```

### 為什麼這樣做不會讓快取失效

直覺上「每次成交都清快取」會讓快取在秒殺期間形同虛設。實際上不會：

1. **清除次數受限於庫存量。** 賣完就不再有成交，1000 件庫存最多清 1000 次。
   而讀取可能有數十萬次。
2. **清除後的下一波讀取由 Single Flight 收斂成一次查詢。**
   §5.1 已經證明這個保護有效。

加上成交失效之後重跑主要測試，DB Queries 仍是 **1**、命中率 **99.2%**。

只有**成功**成交才清快取。失敗（庫存不足）不清 ——
秒殺賣完後 98% 的請求都走失敗路徑，清了等於自廢快取。
有測試釘住這兩件事。

---

## 7. 已知限制

### 庫存顯示仍可能過期

多個 API Instance 的情況下（Stage 8），A 機器的成交會清 Redis 的快取，
這部分沒問題 —— 快取是共用的。但：

- `KeyedLock` 是**行程內**的鎖。N 台機器就有 N 個各自獨立的鎖，
  Single Flight 的保護效果降為 1/N。屆時需要 Redis 分散式鎖。
- `InMemoryMetricsCollector` 同樣是行程內的，每台機器只看得到自己的數字。

這兩點都在程式碼註解中標記，是 Stage 8 的具體教材。

### 快取故障時的行為

`RedisCacheService` 的每個操作都包在 try/catch 中，
Redis 掛掉時退化成直接查資料庫，不會讓請求失敗。

但這也意味著 **Redis 故障會表現為「系統突然變慢」而不是「系統壞掉」**。
所以錯誤有獨立計數（`CacheErrors`）並記錄到 log —— 靜默地失去快取是很難察覺的。

---

## 8. 結論

| 問題 | 加 Redis 前 | 加 Redis 後 |
|---|---:|---:|
| 5000 次讀取的 DB 查詢數 | 5000 | **1** |
| RPS | 2856 | **15004** |
| P99 | 1562 ms | **46 ms** |
| 冷啟動瞬間的 DB 查詢數 | 77 | **1** |
| 查詢不存在資料的 DB 查詢數 | 5000 | **200** |

讀取路徑的資料庫壓力基本上被消除了。

**但搶購的寫入路徑完全沒有改善** —— Stage 3 量到的 118 RPS 天花板還在，
因為所有成交仍然要在資料庫的同一列上排隊。

Stage 5 的 Message Queue 要處理的正是這件事：
讓 API 不必等待資料庫完成寫入就能回應。

---

## 9. 重現步驟

```powershell
# 1. 準備 Redis（若沒有現成的）
docker compose up -d redis
#    然後把 appsettings.Development.json 的 Redis:Configuration 改成 localhost:6379

# 2. Before：關閉快取
$env:ASPNETCORE_ENVIRONMENT="Development"
$env:Cache__Enabled="false"
dotnet run -c Release --project src/FlashSale.Api/FlashSale.Api.csproj --no-launch-profile --urls "http://localhost:5080"
.\tests\load\k6\Run-CacheTest.ps1 -Label "cache-off"

# 3. After：開啟快取
$env:Cache__Enabled="true"
dotnet run -c Release --project src/FlashSale.Api/FlashSale.Api.csproj --no-launch-profile --urls "http://localhost:5080"
.\tests\load\k6\Run-CacheTest.ps1 -Label "cache-on"

# 4. Cache Stampede
$env:Cache__EnableSingleFlight="false"
#    重啟 API 後
.\tests\load\k6\Run-CacheTest.ps1 -Label "stampede-unprotected"

# 5. Cache Penetration
$env:Cache__EnableNullCaching="false"
#    重啟 API 後
.\tests\load\k6\Run-CacheTest.ps1 -Label "penetration-unprotected" -ProductId 999999
```

原始 k6 summary JSON 會寫到 `tests/load/k6/results/`（已 gitignore）。
