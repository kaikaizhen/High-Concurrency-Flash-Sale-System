# Stage 2 — Race Condition 重現紀錄

> Branch：`feature/race-condition`
> 日期：2026-08-28
>
> **本階段不修正問題，只證明問題存在。**

---

## 1. 目的

證明 Stage 1 的 Baseline 搶購邏輯在併發下會產生資料不一致，並用數據說明
「單一 Request 沒錯，錯的是同時執行」。

受測程式碼：[`FlashSaleService.PurchaseAsync`](../../src/FlashSale.Api/Services/FlashSaleService.cs)

```text
Read Product  →  Stock >= Qty ?  →  Stock -= Qty  →  UPDATE  →  INSERT Order
```

這四步之間沒有 Transaction、Lock、Atomic Update 或版本控制。

---

## 2. 測試環境

| 項目 | 內容 |
|---|---|
| API | ASP.NET Core 9 / Kestrel，單一 Instance，`http://localhost:5080` |
| 執行模式 | `dotnet run`（Development，未經 Release 最佳化） |
| 資料庫 | SQL Server（遠端主機），EF Core 9 |
| 壓測工具 | k6 v2.2.0 |
| 壓測機 | 與 API 同一台（Windows 11） |
| 負載模型 | `per-vu-iterations`，每個 VU 只送 **1 次**請求 |

選 `per-vu-iterations` 而不是持續打流量，是因為要讓 N 個請求盡可能**同時**抵達，
把「多個請求讀到同一個庫存值」的視窗放到最大。

---

## 3. 測試方式

```powershell
# 一次跑完四個併發等級
.\tests\load\k6\Run-RaceCondition.ps1 -Stock 100 -Vus 10,100,500,1000

# 單一情境
.\tests\load\k6\Run-RaceCondition.ps1 -Stock 10 -Vus 100
```

每個併發等級都會**建立一個全新商品**，所以庫存與訂單數乾淨可比對，不受前次執行污染。

腳本：

- [`tests/load/k6/race-condition.js`](../../tests/load/k6/race-condition.js) — k6 腳本
- [`tests/load/k6/Run-RaceCondition.ps1`](../../tests/load/k6/Run-RaceCondition.ps1) — 建商品 → 壓測 → 讀回結果

### 指標定義

| 指標 | 算法 | 意義 |
|---|---|---|
| `Success` | HTTP 200 數 | 系統認為成功的搶購 |
| `Rejected` | HTTP 409 數 | 系統**有意識地**拒絕（庫存不足） |
| `Errored` | 其他（含連線失敗） | 壓力下暴露的故障 |
| `Orders` | 資料庫實際訂單數 | 真正賣出幾件 |
| `StockConsumed` | `StockBefore - StockAfter` | 庫存實際被扣掉幾件 |
| `LostUpdate` | `Orders - StockConsumed` | **建了單但庫存沒扣到的數量** |
| `Oversold` | `max(0, Orders - StockBefore)` | **賣出數量超過庫存的數量** |

---

## 4. 對照組：依序送出

同一份程式碼、同一個資料、**唯一的差別是不併發**。

```text
Stock Before      10
Requests          100（依序，前一個回應後才送下一個）
Success (200)     10
Rejected (409)    90
Errored           0
Orders            10
Stock After       0
```

**完全正確。** 這是關鍵 —— 程式邏輯本身沒有 bug。

---

## 5. 併發結果

### 5.1 Stock = 100

| Concurrent Users | Requests | Success | Rejected | Errored | Orders | Stock After | Stock Consumed | Lost Update | Oversold | P95 (ms) | P99 (ms) |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 10 | 10 | 10 | 0 | 0 | 10 | 98 | 2 | **8** | 0 | 45.2 | 46.0 |
| 100 | 100 | 100 | 0 | 0 | 100 | 96 | 4 | **96** | 0 | 498.9 | 513.2 |
| 500 | 500 | 207 | 0 | 293 | 207 | 95 | 5 | **202** | **107** | 936.9 | 1026.4 |
| 1000 | 1000 | 211 | 0 | 789 | 211 | 91 | 9 | **202** | **111** | 914.4 | 1080.5 |

### 5.2 庫存小於請求數（乾淨的超賣證據，無連線錯誤干擾）

| Concurrent Users | Stock Before | Requests | Success | Rejected | Errored | Orders | Stock After | Lost Update | Oversold | P95 (ms) | P99 (ms) |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 100 | 50 | 100 | 100 | 0 | 0 | 100 | 48 | **98** | **50** | 388.6 | 405.6 |
| 100 | 10 | 100 | 100 | 0 | 0 | 100 | 7 | **97** | **90** | 523.9 | 540.2 |

**庫存 10 件，賣出 100 件，而且庫存還剩 7。**

`Rejected = 0` 特別值得注意 —— 系統**從頭到尾沒有察覺庫存不足**。
100 個請求全部讀到 `Stock = 10`，全部通過 `Stock >= Quantity` 檢查。

---

## 6. 觀察到的問題

### 問題 1：Lost Update（庫存扣減互相覆寫）

10 個併發只扣掉 2 件庫存，100 個併發只扣掉 4 件。

原因在 EF Core 產生的 SQL：

```sql
UPDATE [Products]
SET [CreatedAt] = @p0, [Name] = @p1, [Price] = @p2, [Stock] = @p3
WHERE [Id] = @p4;
```

`Stock` 是被**整個覆寫**成應用程式算好的值，不是在資料庫端做減法。所以：

```text
Request A   讀到 Stock = 100  →  算出 99  →  UPDATE Stock = 99
Request B   讀到 Stock = 100  →  算出 99  →  UPDATE Stock = 99
Request C   讀到 Stock = 100  →  算出 99  →  UPDATE Stock = 99
```

三筆訂單，庫存只掉 1。**最後一個寫入者贏，前面的通通消失。**

### 問題 2：Oversell（超賣）

因為庫存幾乎扣不下去，`Stock >= Quantity` 這個檢查對後續請求形同虛設。
庫存 10 件的情況下賣出 100 件，超賣 90 件。

真正的根因：

```text
Read Stock   ─┐
              ├─ 這中間沒有任何保護，其他人可以插隊
Write Stock  ─┘
```

`Read → Modify → Write` 不是 Atomic Operation。

### 問題 3：500 併發以上，瓶頸從資料庫轉移到連線層

| VUs | Errored | 錯誤內容 |
|---:|---:|---|
| 500 | 293 | `connectex: No connection could be made because the target machine actively refused it.` |
| 1000 | 789 | 同上 |

API 程序並沒有掛掉（測試結束後仍可正常回應），是 Kestrel 的 accept backlog
被瞬間湧入的連線塞爆。**這代表 500 VU 以上的數據無法反映真實的資料庫競爭程度** ——
只有約 210 個請求真正進到應用程式。

這也是為什麼要補做 §5.2 那組低庫存測試：讓所有請求都成功抵達，才看得到乾淨的超賣。

### 附帶觀察：Latency

| VUs | P95 | P99 |
|---:|---:|---:|
| 10 | 45 ms | 46 ms |
| 100 | 499 ms | 513 ms |
| 500 | 937 ms | 1026 ms |

10 → 100 併發，P95 漲了 11 倍。每個請求要跑 3 次資料庫往返
（SELECT Product、UPDATE Product、INSERT Order），且 API 與 SQL Server 不同機。

---

## 7. 為什麼「加個 Transaction」不會解決問題

這是最容易誤會的地方，先記錄下來，Stage 3 會用實驗驗證。

把上面四個步驟包進 `BEGIN TRAN / COMMIT`，在 SQL Server 預設的
`READ COMMITTED` 隔離等級下，會變成：

```text
Tx A: SELECT Stock → 100      Tx B: SELECT Stock → 100
      (共享鎖讀完就釋放)             (共享鎖讀完就釋放)
Tx A: UPDATE Stock = 99
Tx B: UPDATE Stock = 99
Tx A: COMMIT                  Tx B: COMMIT
```

**超賣照樣發生。**

`READ COMMITTED` 保證的是「不會讀到未 commit 的髒資料」，
**不保證「你讀到的值到你寫回去為止沒被別人改過」**。

要靠 Transaction 解決，必須額外加上明確的鎖
（`UPDLOCK`、`SERIALIZABLE`、悲觀鎖），而那會帶來 Lock Waiting 與 Throughput 代價 ——
這正是 Stage 3 要量測並比較的東西。

Transaction 在這裡真正該修的是**另一個問題**：目前扣庫存與建訂單是兩次獨立的
`SaveChanges`，如果扣完庫存後建單失敗，庫存就憑空少了一件卻沒有對應訂單。
那是**原子性**問題，與這裡的**併發**問題是兩回事。

---

## 8. 結論

| 問題 | 是否重現 | 證據 |
|---|---|---|
| Race Condition 存在 | ✅ | 依序 100 請求 → 10 筆訂單；併發 100 請求 → 100 筆訂單 |
| Lost Update | ✅ | 100 筆訂單，庫存只扣 3 件 |
| Oversell | ✅ | 庫存 10 件賣出 100 件，超賣 90 件 |
| 可穩定重現 | ✅ | 多次執行皆超賣，數值有波動但方向一致 |

單一 Request 的邏輯完全正確。問題出在
**`Read → Modify → Write` 不是 Atomic Operation**。

Stage 3 將實作並比較 Transaction、Optimistic Concurrency、Atomic Update 三種解法。

---

## 9. 重現步驟

```powershell
# 1. 啟動 API
$env:ASPNETCORE_ENVIRONMENT="Development"
dotnet run --project src/FlashSale.Api/FlashSale.Api.csproj --no-launch-profile --urls "http://localhost:5080"

# 2. 另開一個終端機執行壓測
.\tests\load\k6\Run-RaceCondition.ps1 -Stock 10 -Vus 100
```

原始 k6 summary JSON 會寫到 `tests/load/k6/results/`（已 gitignore）。

### 已知的環境陷阱

- **`Run-RaceCondition.ps1` 必須以 UTF-8 with BOM 儲存。** PowerShell 5.1 會用系統
  ANSI codepage 讀取沒有 BOM 的 `.ps1`，中文註解變成亂碼後會破壞語法解析，
  導致統計欄位算出 `$null`。
- 腳本內 k6 的呼叫刻意把 `$ErrorActionPreference` 暫時降為 `Continue`。高併發下
  k6 會把連線失敗寫進 stderr，PowerShell 5.1 會將原生指令的 stderr 包成 ErrorRecord
  而中斷整個測試 —— 但連線失敗正是要記錄的現象，不是執行失敗。
