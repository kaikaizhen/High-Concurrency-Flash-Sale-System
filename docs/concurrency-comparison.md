# Stage 3 — Concurrency Control 方案比較

> Branch：`feature/concurrency-control`
> 日期：2026-08-28
>
> 前置：[Stage 2 Race Condition 重現紀錄](load-test/race-condition.md)

---

## 1. 目的

Stage 2 證明了 Baseline 會超賣，根因是
**`Read → Modify → Write` 不是 Atomic Operation**。

本階段實作三種解法，在同一台機器、同一次測試中比較其正確性與代價，
最後選定專案的主要方案。

---

## 2. 三個版本

四種實作並存於
[`Services/FlashSaleStrategies/`](../src/FlashSale.Api/Services/FlashSaleStrategies/)，
由請求的 `strategy` 欄位決定走哪一個。Baseline 保留作為對照組。

### Version A — Transaction + 悲觀鎖

```text
BEGIN TRAN
  SELECT ... WITH (UPDLOCK, ROWLOCK)    ← 更新鎖持有到交易結束
  檢查庫存
  UPDATE Stock
  INSERT Order
COMMIT
```

**關鍵在 UPDLOCK，不在 Transaction 本身。** 見 §6。

### Version B — Optimistic Concurrency

`Product` 新增 `rowversion` 欄位並設為 EF Core 的 Concurrency Token，
變更追蹤送出的 UPDATE 會自動附帶版本條件：

```sql
UPDATE Products SET Stock = @stock
WHERE Id = @id AND RowVersion = @original
```

影響列數為 0 → `DbUpdateConcurrencyException` → 重新讀取後重試，
最多 `GlobalConstants.MaxConcurrencyRetryCount`（10）次。

### Version C — Atomic Update

```sql
UPDATE Products
SET Stock = Stock - @quantity
WHERE Id = @productId AND Stock >= @quantity
```

以 `AffectedRows` 判斷成功與否。
**應用程式在成功路徑上完全不讀取庫存** —— 檢查與減法在資料庫端的同一個語句內完成。

### 共同的交易邊界

三個版本都把「扣庫存」與「建訂單」放在同一個交易內。

這是刻意的：Transaction 與併發控制是**兩個正交的問題**。
交易解決的是原子性（不會出現「庫存少了但沒有訂單」），
併發控制解決的是 Race Condition。三個版本共用相同的交易邊界，
差異就純粹是「如何處理同一列的競爭」，比較才成立。

---

## 3. 測試方法

```powershell
.\tests\load\k6\Run-ConcurrencyComparison.ps1 -Stock 100  -Iterations 5000 -Vus 200
.\tests\load\k6\Run-ConcurrencyComparison.ps1 -Stock 5000 -Iterations 5000 -Vus 200
```

| 項目 | 內容 |
|---|---|
| API | ASP.NET Core 9，**Release** 組態，單一 Instance |
| 資料庫 | SQL Server（遠端主機），EF Core 9 |
| 壓測工具 | k6 v2.2.0，`shared-iterations` |
| 負載 | 5000 次請求，固定 200 個同時連線 |

### 為什麼不用 Stage 2 的 `per-vu-iterations`

Stage 2 用 1000 個 VU 各送 1 次請求，結果 789 個請求撞在 Kestrel 的
accept backlog 上根本沒進到應用程式，量到的 Latency 沒有意義。

Stage 3 要比較的是三種做法的效能代價，所以把同時連線數壓在連線層撐得住的
範圍（200），用 5000 次迭代分攤下去 —— 競爭同一列庫存的壓力完全不減，
但所有請求都真正進到應用程式。

### 為什麼要做兩組實驗

第一組（Stock = 100）是真實的秒殺情境，但**吞吐量無法直接比較**：
Baseline 成功 5000 次（做了 5000 次寫入），另外三個只成功 100 次
（4900 次快速拒絕）。工作量差 50 倍。

第二組（Stock = 5000）讓庫存足夠所有請求成交，工作量一致，
才能比較純粹的併發控制開銷。

---

## 4. 實驗一：秒殺情境（Stock = 100、5000 個請求）

### 正確性

| 方法 | Orders | Stock After | Lost Update | Oversold | 驗收 |
|---|---:|---:|---:|---:|:---:|
| Baseline | 5000 | 9 | 4909 | **4900** | ❌ FAIL |
| Transaction | 100 | 0 | 0 | 0 | ✅ PASS |
| Optimistic | 100 | 0 | 0 | 0 | ✅ PASS |
| Atomic | 100 | 0 | 0 | 0 | ✅ PASS |

計畫 §8 的驗收條件是 `Orders <= 100` 且 `Stock >= 0`，
理想結果 `Orders = 100`、`Stock = 0`。**三個版本都達成理想結果。**

Baseline 賣出 5000 件、超賣 4900 件，庫存還剩 9 —— 與 Stage 2 的結論一致。

### 效能

| 方法 | Success | Rejected | 總耗時 | RPS | Avg | P95 | P99 | Max |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Baseline | 5000 | 0 | 24.7s | 212.9 | 924 ms | 1212 ms | 3386 ms | 3624 ms |
| Transaction | 100 | 4900 | 6.5s | 806.9 | 244 ms | 347 ms | 907 ms | 947 ms |
| Optimistic | 100 | 4900 | 10.6s | 488.5 | 408 ms | **2858 ms** | 2997 ms | 3158 ms |
| **Atomic** | 100 | 4900 | **4.8s** | **1121.6** | **176 ms** | **250 ms** | **659 ms** | **688 ms** |

Atomic 的 RPS 是 Transaction 的 1.4 倍、Optimistic 的 2.3 倍。

差距來自**拒絕路徑的成本**：庫存賣完後，Atomic 只需要一個 UPDATE 回傳 0 列就能拒絕；
Transaction 必須先排隊取得 UPDLOCK 才知道庫存不足；
Optimistic 則要先讀一次才知道。秒殺情境下 98% 的請求都走拒絕路徑，
這個成本差異被放大 50 倍。

---

## 5. 實驗二：工作量一致（Stock = 5000、5000 個請求）

### 結果

| 方法 | Success | Rejected | Orders | Stock After | Lost Update | 總耗時 | RPS | Avg | P95 | P99 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Baseline | 5000 | 0 | 5000 | 4905 | **4905** | 24.1s | 211.5 | 929 ms | 1246 ms | 3384 ms |
| Transaction | 5000 | 0 | 5000 | 0 | 0 | 44.2s | 114.2 | 1713 ms | 2038 ms | 2080 ms |
| Optimistic | **896** | **4104** | 896 | 4104 | 0 | 79.9s | 62.9 | 3158 ms | 4199 ms | 4531 ms |
| **Atomic** | 5000 | 0 | 5000 | 0 | 0 | **42.6s** | **118.3** | 1660 ms | **1929 ms** | 2021 ms |

### 這組數據推翻了一個直覺

**Optimistic 只賣掉 896 件，拒絕了 4104 個請求 —— 而庫存明明還有 4104 件。**

它沒有超賣、沒有 Lost Update，資料完全正確，但它把 82% 付得起錢的客人趕走了。
這些請求收到的是「重試次數用盡」，不是「庫存不足」。

從 API log 統計重試分布（兩組 Optimistic 測試合計 49,286 次衝突）：

| Attempt | 發生衝突的請求數 |
|---:|---:|
| 1 | 5424 |
| 2 | 5312 |
| 3 | 5204 |
| 4 | 5118 |
| 5 | 5010 |
| 6 | 4846 |
| 7 | 4724 |
| 8 | 4635 |
| 9 | 4547 |
| 10 | 4466 |

**衰減幾乎為零。** 第 1 次重試有 5424 個請求撞上衝突，重試到第 10 次仍有 4466 個在撞。
每一輪的衝突機率都維持在 92% 左右，跟重試了幾次無關。

原因：200 個連線持續搶同一列，任何一個成功都會讓其餘 199 個手上的 `RowVersion` 過期。
**在持續高衝突下，樂觀鎖的重試不會收斂。** 提高重試上限只會把 Latency 拉長，
不會提高成交率。

樂觀鎖的前提是「衝突很少見，所以樂觀」。秒殺恰好是衝突率最高的場景 ——
**前提不成立，做法就不成立。**

### Transaction 與 Atomic 幾乎打平

114.2 vs 118.3 RPS。當所有請求都必須成交時，兩者都得在同一列上排隊，
**瓶頸是那一列的排他鎖，不是策略本身**。Atomic 省下的一次 SELECT 往返
只帶來約 4% 的差距。

也就是說：實驗一 1.4 倍的差距，來自拒絕路徑；成交路徑上兩者相當。

### Baseline 的 24 秒不代表它比較快

Baseline 在兩組實驗都是 211~213 RPS。它沒有任何鎖，卻不是最快的 ——
因為它每次都真的做了兩次寫入。而 Transaction/Atomic 在實驗二也做了 5000 次寫入，
卻因為要在同一列上排隊而更慢。

換句話說：**正確性的代價就是那一列的序列化**，這是無法迴避的。

---

## 6. 驗證 Stage 2 的預測：Transaction 本身不夠

Stage 2 的文件 §7 預測「單純把四步包進交易，在 READ COMMITTED 下超賣不會消失」。

Version A 的實作證實了這一點 —— 它必須用
`SELECT ... WITH (UPDLOCK, ROWLOCK)` 才能正確，
用一般的 `SELECT` 包在交易裡是不夠的：

```text
READ COMMITTED 的共享鎖：讀完就釋放
    Tx A: SELECT Stock → 100   (鎖已釋放)
    Tx B: SELECT Stock → 100   (鎖已釋放)
    兩者都算出 99 → 超賣

UPDLOCK：讀取時就取得更新鎖，持有到交易結束
    Tx A: SELECT WITH (UPDLOCK) → 100   (鎖持有中)
    Tx B: SELECT WITH (UPDLOCK) → 等待…
    Tx A: UPDATE 99, COMMIT             (鎖釋放)
    Tx B: SELECT WITH (UPDLOCK) → 99    (讀到最新值)
```

單元測試
[`Transaction_ShouldReadWithUpdateLock_NotPlainRead`](../tests/FlashSale.UnitTests/Services/FlashSaleStrategyTests.cs)
把這件事釘住 —— 如果有人日後把它改回一般讀取，測試會失敗。

---

## 7. 比較表

| 方法 | 正確性 | Latency | DB Lock | 實作複雜度 | 適用場景 |
|---|---|---|---|---|---|
| **Baseline** | ❌ 超賣 4900 件、Lost Update 4905 次 | 快但無意義 | 無 | 最低 | 無。僅作對照組 |
| **Transaction (UPDLOCK)** | ✅ 完全正確 | 秒殺情境 P95 347 ms；全成交 P95 2038 ms | 悲觀鎖，鎖住整段交易 | 中。需要正確使用 hint，錯用 hint 等於沒做 | 一筆交易要動多張表、且流程無法收斂成單一語句時 |
| **Optimistic (rowversion)** | ⚠️ 不會超賣，但高衝突下**大量誤拒**（成交率 18%） | 最差。P95 2858~4199 ms | 無鎖，改以版本檢查 | 高。重試迴圈、Entity 卸離、重試上限調校 | 衝突率低的場景（後台編輯、設定變更）。**不適合秒殺** |
| **Atomic Update** | ✅ 完全正確 | 最佳。秒殺情境 P95 250 ms | 只在單一 UPDATE 語句期間持有列鎖 | 最低。一個 SQL 語句，無重試邏輯 | 單一資料列的計數器型競爭 —— 庫存、額度、票券 |

---

## 8. 選定方案

> **Atomic Update（Version C）作為本專案的主要方案。**

`CreateFlashSaleDtoModel.Strategy` 的預設值為 `Atomic`，
未指定策略的請求一律走這條路徑。單元測試 `DefaultStrategy_ShouldBeAtomic` 釘住此決定。

理由：

1. **正確性與另外兩者相同**，且兩組實驗都達成理想結果（Orders = 100、Stock = 0）。
2. **秒殺情境下效能最好**：RPS 是 Transaction 的 1.4 倍、Optimistic 的 2.3 倍，
   P95 只有它們的 72% / 9%。
3. **實作最單純**：一個 SQL 語句，沒有重試迴圈、沒有鎖 hint、沒有 Entity 狀態管理。
   在這裡「簡單」不只是美學問題 —— 另外兩者各有一個容易踩錯而失效的細節
   （UPDLOCK 忘了加、重試後忘了卸離 Entity），Atomic 沒有這種失敗模式。
4. **鎖的持有時間最短**：只在單一 UPDATE 語句期間，不跨越應用程式的往返。

其餘三個版本保留在程式碼中，不是為了生產使用，而是為了讓後續 Stage
能隨時重跑比較 —— 例如 Stage 4 加入 Redis 之後，可以驗證結論是否改變。

### 但這不是終點

實驗二顯示 Atomic 也只有 118 RPS，因為所有請求仍然要在資料庫的同一列上排隊。
**併發控制解決了正確性，沒有解決吞吐量。**

要突破這個瓶頸，必須讓大多數請求根本不碰到那一列 —— 這就是
Stage 4（Redis）與 Stage 5（Message Queue）要處理的問題。

---

## 9. 本階段順帶處理的事

加入 `rowversion` 之後，`PUT /api/products/{id}` 走的 EF 變更追蹤更新也會帶版本檢查。
若在讀取與寫入之間有搶購改動了同一筆商品，這個更新會失敗。

原本會變成 500，已改為由 `ProductService` 判斷後丟出 `BusinessException` → **409**，
這是可預期的商業狀況而非系統錯誤。

---

## 10. 重現步驟

```powershell
# 1. 套用 migration（新增 rowversion 欄位）
$env:ASPNETCORE_ENVIRONMENT="Development"
dotnet ef database update --project src/FlashSale.Api/FlashSale.Api.csproj

# 2. 啟動 API（Release，避免 Debug 組態干擾效能數據）
dotnet run -c Release --project src/FlashSale.Api/FlashSale.Api.csproj --no-launch-profile --urls "http://localhost:5080"

# 3. 另開終端機執行比較
.\tests\load\k6\Run-ConcurrencyComparison.ps1 -Stock 100  -Iterations 5000 -Vus 200
.\tests\load\k6\Run-ConcurrencyComparison.ps1 -Stock 5000 -Iterations 5000 -Vus 200
```

原始 k6 summary JSON 會寫到 `tests/load/k6/results/`（已 gitignore）。

指定策略也可以直接打 API：

```bash
curl -X POST http://localhost:5080/api/flash-sale/1 \
  -H "Content-Type: application/json" \
  -d '{"userId":1,"quantity":1,"strategy":"Atomic"}'
```
