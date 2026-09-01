# High-Concurrency Flash Sale System

以 ASP.NET Core Web API 為主體的高併發學習專案。

學習原則：**先看到問題，再導入技術解決問題。**

完整規劃請見 [docs/計畫.md](docs/計畫.md)，架構規範請見
[docs/architecture/Backend_Architecture_Guideline.md](docs/architecture/Backend_Architecture_Guideline.md)。

---

## 目前進度

| Stage | Branch | 狀態 |
|---|---|---|
| 1. CRUD Baseline | `feature/crud` | ✅ 完成 |
| 2. Race Condition | `feature/race-condition` | ✅ 完成 — [結果](docs/load-test/race-condition.md) |
| 3. Concurrency Control | `feature/concurrency-control` | ✅ 完成 — [比較](docs/concurrency-comparison.md) |
| 4. Redis | `feature/redis` | ✅ 完成 — [結果](docs/load-test/redis.md) |
| 5. Message Queue | `feature/message-queue` | ✅ 完成 — [結果](docs/load-test/queue.md) |
| 6. Idempotency | `feature/idempotency` | ✅ 完成 — [結果](docs/idempotency.md) |
| 7. Rate Limit | `feature/rate-limit` | ✅ 完成 — [結果](docs/rate-limit.md) |
| 8. Multi Instance | `feature/multi-instance` | 未開始 |
| 9. Load Test | `feature/load-test` | 未開始 |
| 10. Observability + Optimization | `feature/observability-optimization` | 未開始 |

---

## Stage 進度圖

```mermaid
flowchart LR
    S1["Stage 1<br/>CRUD Baseline"] --> S2["Stage 2<br/>Race Condition"]
    S2 --> S3["Stage 3<br/>Concurrency Control"]
    S3 --> S4["Stage 4<br/>Redis"]
    S4 --> S5["Stage 5<br/>Message Queue"]
    S5 --> S6["Stage 6<br/>Idempotency"]
    S6 --> S7["Stage 7<br/>Rate Limit"]
    S7 --> S8["Stage 8<br/>Multi Instance"]
    S8 --> S9["Stage 9<br/>Load Test"]
    S9 --> S10["Stage 10<br/>Observability"]

    classDef done fill:#2f9e44,stroke:#2f9e44,color:#fff
    classDef todo fill:#495057,stroke:#495057,color:#fff
    class S1,S2,S3,S4,S5,S6,S7 done
    class S8,S9,S10 todo
```

---

## Git 原則

```text
main          穩定版本，每個重要里程碑由 develop 合併並打 tag
  └── develop 日常整合分支
        └── feature/*   每個 Stage 一個，完成後 --no-ff 合併回 develop
```

遠端：<https://github.com/kaikaizhen/High-Concurrency-Flash-Sale-System>

> Stage 1–5 期間刻意只使用本地 Git（見 [計畫 §4.1](docs/計畫.md)），
> 於 2026-08-28 Stage 5 完成後才接上遠端。

---

## 設定檔與機密

版控中**不存在任何真實連線字串或密碼**。

| 檔案 | 進版控 | 內容 |
|---|---|---|
| `src/FlashSale.Api/appsettings.json` | ✅ | 只有 Logging 等非機密設定 |
| `src/FlashSale.Api/appsettings.Development.Example.json` | ✅ | 範本，值全部是佔位字串 |
| `src/FlashSale.Api/appsettings.Development.json` | ❌ (`.gitignore`) | 本機真實連線資訊 |

第一次 clone 或換機器時：

```bash
cd src/FlashSale.Api
cp appsettings.Development.Example.json appsettings.Development.json
# 然後把 appsettings.Development.json 內的佔位值換成實際連線資訊
```

Example 檔已預留 `Redis` 與 `RabbitMq` 區塊，Stage 4 / Stage 5 會用到，
目前程式尚未讀取。

---

## 專案結構

```text
HighConcurrencyFlashSale/
│
├── src/
│   ├── FlashSale.Api/          # Controller → Service → Repository → Database
│   │   ├── Controllers/
│   │   ├── Services/  (+ Interfaces/ + FlashSaleStrategies/)
│   │   ├── Repositories/  (+ Interfaces/)
│   │   ├── Models/  Entities / Dtos / Params / ViewModels / Messages
│   │   ├── Mappings/           # AutoMapper Profile
│   │   ├── Data/               # AppDbContext / Configurations / Migrations / Interceptors
│   │   ├── Common/             # Enums / Constants / Exceptions
│   │   ├── Infrastructure/     # Cache (Redis) / Messaging (RabbitMQ) / Diagnostics
│   │   ├── Options/            # Redis / Cache / RabbitMq
│   │   ├── Extensions/         # DependencyInjectionExtensions
│   │   ├── Middlewares/        # GlobalExceptionMiddleware
│   │   └── Program.cs
│   │
│   └── FlashSale.Worker/       # RabbitMQ Consumer（Stage 5 起）
│       └── OrderCreatedConsumer.cs
│
├── tests/
│   ├── FlashSale.UnitTests/
│   └── load/k6/                # k6 腳本與 PowerShell 執行器
├── docs/
└── docker-compose.yml          # redis + rabbitmq（Stage 8 會再加 nginx）
```

計畫 §18 規劃的 `FlashSale.Application` / `Domain` / `Infrastructure` 專案尚未建立 ——
依計畫「目錄與 Infrastructure 應隨著 Stage 演進逐步加入」，
目前 `FlashSale.Worker` 直接參考 `FlashSale.Api` 以共用 Entity、
Repository 與 Messaging。若專案再長大，該把共用部分抽成獨立的類別庫。

### 三層式架構的資料流

```mermaid
flowchart LR
    Client(["Client"]) -->|HTTP| Controller
    Controller -->|ParamModel → DtoModel| Service
    Service -->|DtoModel → Entity| Repository
    Repository --> DB[("SQL Server")]
    Service -.->|Cache Aside| Redis[("Redis")]

    classDef layer fill:#1971c2,stroke:#1971c2,color:#fff
    classDef store fill:#495057,stroke:#495057,color:#fff
    class Controller,Service,Repository layer
    class DB,Redis store
```

Controller 只認 ParamModel / ViewModel，Service 承擔商業規則，
Repository 專責資料存取；Entity 絕不跨出 Repository/Service 邊界外流到 Client。

---

## 執行

### 前置

- .NET 9 SDK
- 可連線的 SQL Server
- 可連線的 Redis（`docker compose up -d redis` 可起一個本機的）
- 可連線的 RabbitMQ（`docker compose up -d rabbitmq`），AMQP 埠 **5672**
- k6（壓測用）：`winget install --id GrafanaLabs.k6`

### 建立資料庫

```bash
dotnet tool install --global dotnet-ef

$env:ASPNETCORE_ENVIRONMENT="Development"
dotnet ef database update --project src/FlashSale.Api/FlashSale.Api.csproj
```

### 啟動

```bash
$env:ASPNETCORE_ENVIRONMENT="Development"
dotnet run --project src/FlashSale.Api/FlashSale.Api.csproj --no-launch-profile --urls "http://localhost:5080"
```

Swagger UI：<http://localhost:5080/swagger>

Stage 5 的非同步搶購還需要啟動 Worker（另一個終端機）：

```bash
$env:DOTNET_ENVIRONMENT="Development"
dotnet run -c Release --no-build --project src/FlashSale.Worker/FlashSale.Worker.csproj --no-launch-profile
```

> Worker 建置時會連帶重建 API 專案，若 API 正在執行會鎖住檔案。
> 先 `dotnet build -c Release` 一次，再用 `--no-build` 啟動兩者。

### 測試

```bash
dotnet test
```

---

## API

Base URL：`http://localhost:5080`

### `GET /api/products`

取得所有商品。

```json
[
  { "id": 1, "name": "iPhone", "price": 30000.00, "stock": 10, "createdAt": "2026-08-28T03:21:18.675Z" }
]
```

### `GET /api/products/{id}`

單一商品。找不到回 `404`。

### `POST /api/products`

```json
{ "name": "iPhone", "price": 30000, "stock": 10 }
```

`201 Created`。名稱重複回 `409`。

### `PUT /api/products/{id}`

```json
{ "name": "iPhone 16", "price": 25000, "stock": 5 }
```

`200 OK`。找不到回 `404`。

### `POST /api/orders`

一般下單，**只建立訂單（`Pending`），不扣庫存**。

```json
{ "userId": 1, "productId": 1, "quantity": 1 }
```

`201 Created`。商品不存在回 `404`。

### `GET /api/orders?productId={id}` / `GET /api/orders/{id}`

查詢訂單。`productId` 版本用來驗證「送出 N 次 Request 後究竟建立了幾筆訂單」。

### `POST /api/flash-sale/{productId}`

搶購。**扣庫存 → 建立訂單（`Completed`）**，兩者在同一個交易內。

```json
{ "userId": 1, "quantity": 1, "strategy": "Atomic" }
```

選填 header：
- `Idempotency-Key: <uuid>` —— 重送保護（Stage 6）
- `X-User-Id: <id>` —— per-User 限流的分區依據（Stage 7），未帶時退回 per-IP

| 狀況 | HTTP | 回應 |
|---|---|---|
| 成功（同步） | `200` | `status: "Completed"` + `order` |
| 成功（非同步） | `202` | `status: "Queued"` + `requestId`，`order` 為 `null` |
| 重送（相同 Key） | `200`/`202` | 回放原本的回應 + `Idempotency-Replayed: true` |
| 商品不存在 | `404` | |
| 庫存不足 | `409` | |
| 樂觀鎖重試用盡 | `409` | |
| 相同 Key 正在處理中 | `409` | |
| 超出限流額度 | `429` | + `Retry-After` header |

```json
{ "status": "Queued", "requestId": "d5455cdc-...", "order": null }
```

`strategy` 為選填，預設 `Atomic`（Stage 3 選定的主要方案）。
可選 `Baseline` / `Transaction` / `Optimistic` / `Atomic` / `AtomicQueued`。

`AtomicQueued` 是 Stage 5 的非同步版本：庫存仍然同步扣減，
訂單交由 Worker 建立，因此回 `202` 且此刻資料庫裡還沒有訂單。

> `Baseline` 是 Stage 1 的無保護版本，**會超賣**，僅作為對照組保留。

#### 搶購請求流程（以預設的 Atomic 為例）

```mermaid
sequenceDiagram
    participant C as Client
    participant Ctrl as FlashSaleController
    participant Svc as FlashSaleService
    participant St as AtomicFlashSalePurchaseStrategy
    participant DB as SQL Server
    participant R as Redis

    C->>Ctrl: POST /api/flash-sale/{productId}
    Ctrl->>Svc: PurchaseAsync(dto)
    Svc->>St: PurchaseAsync(dto)

    St->>DB: BEGIN TRAN
    St->>DB: UPDATE Products SET Stock = Stock - qty<br/>WHERE Id = @id AND Stock >= qty

    alt AffectedRows = 1（庫存足夠）
        St->>DB: INSERT Order
        St->>DB: COMMIT
        St-->>Svc: Order
        Svc->>R: RemoveAsync(product:{id})  ⟵ 使商品快取失效
        Svc-->>Ctrl: OrderDtoModel
        Ctrl-->>C: 200 OK
    else AffectedRows = 0（庫存不足 / 商品不存在）
        St->>DB: ROLLBACK
        St-->>Svc: throw BusinessException / NotFoundException
        Svc-->>Ctrl: (exception)
        Ctrl-->>C: 409 / 404
    end
```

檢查與扣減是**同一個 SQL 語句**，由資料庫的列鎖保證原子性，
應用程式完全不需要先讀取庫存 —— 這是它比 Transaction / Optimistic 兩版更快的原因。
只有成交後才清 Redis 快取，失敗（庫存不足）不清，見
[Stage 4 併發 §6](docs/load-test/redis.md#6-過程中發現並修正的問題)。

### `GET /api/diagnostics/metrics` / `POST /api/diagnostics/metrics/reset`

壓測用的觀測端點。回傳實際送到資料庫的命令數與快取命中率。

```json
{ "dbCommands": 1, "cacheHits": 4999, "cacheMisses": 42, "cacheErrors": 0, "cacheHitRate": 0.9917 }
```

`dbCommands` 由 EF Core Interceptor 累加，是實際值而非估算。

> 計數器存在單一 Instance 的記憶體中，Stage 8 導入多 Instance 後需要改寫。

### `GET /api/diagnostics/queue`

Stage 5：佇列待處理訊息數，用來觀察削峰填谷。

```json
{ "pendingOrders": 2470, "pendingRetries": 0, "deadLettered": 0, "available": true }
```

### 錯誤格式

所有商業錯誤由 `GlobalExceptionMiddleware` 統一輸出：

```json
{ "status": 409, "message": "Insufficient stock.", "traceId": "0HNO4MDFJQUUN:00000001" }
```

| Exception | HTTP |
|---|---|
| `NotFoundException` | 404 |
| `BusinessException` | 409 |
| 其他 | 500 |

---

## Stage 1 驗證結果

計畫 §6「驗證」段落的手動測試，實際執行結果：

```text
建立商品                Stock = 10
依序送出 10 次搶購      10 × HTTP 200
第 11 次                HTTP 409  Insufficient stock.

最終 Stock              0
最終 Order 數量         10
```

符合預期。

### Baseline 的已知缺陷（Stage 3 起改為對照組保留）

[BaselineFlashSalePurchaseStrategy](src/FlashSale.Api/Services/FlashSaleStrategies/BaselineFlashSalePurchaseStrategy.cs)
使用一般 CRUD 思維：

```text
Read Product → Stock > 0 ? → Stock-- → Update → Create Order
```

「讀取庫存」與「寫回庫存」是兩次分開的資料庫往返，中間沒有
Transaction、Lock、Atomic Update 或版本控制。**依序**送出請求不會出錯，
但**併發**送出時多個請求會讀到同一個庫存值，各自通過檢查後各自建單 —— 造成超賣。

Stage 2 已用 k6 證明它，Stage 3 已提供三種正確解法並選定 Atomic Update。
Baseline 之所以保留，是為了讓後續 Stage 隨時能重跑比較 ——
它**永遠不會是預設路徑**，只有明確指定 `"strategy": "Baseline"` 才會走到。

---

## Stage 2 壓力測試

```powershell
# 前置：k6（winget install --id GrafanaLabs.k6）
# 需要 API 已在 http://localhost:5080 執行

.\tests\load\k6\Run-RaceCondition.ps1 -Stock 100 -Vus 10,100,500,1000
.\tests\load\k6\Run-RaceCondition.ps1 -Stock 10 -Vus 100
```

結果摘要（庫存 10 件、100 個併發請求）：

```text
依序送出   ->  Orders = 10   Stock = 0    Rejected = 90   正確
併發送出   ->  Orders = 100  Stock = 7    Rejected = 0    超賣 90 件
```

完整紀錄與原因分析：[docs/load-test/race-condition.md](docs/load-test/race-condition.md)

---

## Stage 3 併發控制比較

```powershell
.\tests\load\k6\Run-ConcurrencyComparison.ps1 -Stock 100  -Iterations 5000 -Vus 200
.\tests\load\k6\Run-ConcurrencyComparison.ps1 -Stock 5000 -Iterations 5000 -Vus 200
```

秒殺情境（庫存 100、5000 個請求、200 個同時連線）：

| 方法 | Orders | Stock | 正確性 | RPS | P95 |
|---|---:|---:|:---:|---:|---:|
| Baseline | 5000 | 9 | ❌ 超賣 4900 | 213 | 1212 ms |
| Transaction (UPDLOCK) | 100 | 0 | ✅ | 807 | 347 ms |
| Optimistic (rowversion) | 100 | 0 | ✅ | 489 | 2858 ms |
| **Atomic Update** | 100 | 0 | ✅ | **1122** | **250 ms** |

**選定方案：Atomic Update**，為 `strategy` 未指定時的預設值。

完整比較與取捨分析：[docs/concurrency-comparison.md](docs/concurrency-comparison.md)

---

## Stage 4 Redis 快取

### Cache Aside + Single Flight（`GET /api/products/{id}`）

```mermaid
flowchart TD
    A(["GET /api/products/{id}"]) --> B{"Cache.Enabled?"}
    B -- 否 --> Z["直接查 SQL Server"]

    B -- 是 --> C["讀 Redis"]
    C -->|Hit：正向值| D["回傳商品"]
    C -->|"Hit：負向快取（null）"| NF["404 NotFound"]
    C -->|Miss| E["取得 Key 層級鎖<br/>（KeyedLock，Single Flight）"]

    E --> F["再讀一次 Redis<br/>（double-check）"]
    F -->|"此時已 Hit<br/>（前面請求已填好）"| D
    F -->|仍然 Miss| G["查 SQL Server"]

    G -->|找到| H["寫入 Redis<br/>TTL = 10s"] --> D
    G -->|查無此商品| I["寫入負向快取<br/>TTL = 3s（短）"] --> NF

    classDef hit fill:#2f9e44,stroke:#2f9e44,color:#fff
    classDef miss fill:#e8590c,stroke:#e8590c,color:#fff
    class D hit
    class G,H,I miss
```

**取得鎖之後必須再讀一次快取**：少了這次 double-check，
排隊的請求還是會一個個查資料庫，只是從併發變串行，查詢次數不會減少
（見 [docs/load-test/redis.md §4](docs/load-test/redis.md#4-cache-aside-的實作)）。

```powershell
$env:Cache__Enabled="false"   # 或 "true"，重啟 API 後
.\tests\load\k6\Run-CacheTest.ps1 -Label "cache-off"
```

5000 次 `GET /api/products/{id}`、200 個同時連線：

| | DB 查詢數 | 命中率 | RPS | P99 |
|---|---:|---:|---:|---:|
| 快取關閉 | 5000 | — | 2856 | 1562 ms |
| **快取開啟** | **1** | 99.2% | **15004** | **46 ms** |

三個進階問題的實測（DB 查詢數）：

| | 無保護 | 有保護 |
|---|---:|---:|
| Cache Stampede（冷啟動 200 併發） | 77 | **1**（Single Flight） |
| Cache Penetration（查不存在的 Id） | 5000 | **200**（負向快取） |

完整分析：[docs/load-test/redis.md](docs/load-test/redis.md)

---

## Stage 5 Message Queue

搶購改為：**同步扣庫存 → 發布事件 → 202 Accepted**，訂單由 Worker 建立。

```mermaid
flowchart LR
    C(["Client"]) -->|POST| API
    API -->|"UPDATE Stock<br/>（同步，決定成敗）"| DB[("SQL Server")]
    API -->|"publish<br/>OrderCreated"| MQ["flashsale.orders"]
    API -->|202 Accepted| C

    MQ --> W["Order Worker"]
    W -->|INSERT Order| DB
    W -->|"可重試失敗"| R["retry queue<br/>TTL 5s"]
    R -.->|TTL 到期送回| MQ
    W -->|"無法解析 /<br/>重試用盡"| DLQ["dead letter queue"]

    classDef svc fill:#1971c2,stroke:#1971c2,color:#fff
    classDef store fill:#495057,stroke:#495057,color:#fff
    classDef bad fill:#c92a2a,stroke:#c92a2a,color:#fff
    class API,W svc
    class DB,MQ,R store
    class DLQ bad
```

**扣庫存不能非同步** —— 它是唯一決定「有沒有買到」的判斷。
先回「成功」再發現賣完，是把超賣從資料錯誤變成對客戶的謊言。

```powershell
.\tests\load\k6\Run-QueueTest.ps1 -Strategy Atomic       -Stock 5000 -Iterations 5000
.\tests\load\k6\Run-QueueTest.ps1 -Strategy AtomicQueued -Stock 5000 -Iterations 5000
```

| | 同步 | 非同步 |
|---|---:|---:|
| API 回應完畢 | 45.1s | 48.6s |
| API RPS | 112 | 104 |
| P99 | 4692 ms | **3067 ms** |
| **資料庫命令數** | 10003 | **5013** |

**API 並沒有變快** —— 瓶頸仍是庫存那一列的排隊，而發布訊息本身也要一次遠端往返。
賺到的是資料庫寫入量減半，以及 API 與訂單處理速度的解耦：

| Worker 每筆處理 | API 回應完畢 | API RPS | 佇列峰值 | 訂單全部落地 |
|---|---:|---:|---:|---:|
| 正常 | 48.6s | 104 | 2470 | 69.8s |
| **100 ms** | **45.8s** | **110** | **4642** | ~590s |

**Worker 慢了 27 倍，API 完全不受影響。** 這就是削峰填谷。

> ⚠️ 本階段引入了**重複訂單**的可能（RabbitMQ 是 at-least-once），
> 刻意留給 Stage 6 用 `MessageId` 解決。

完整分析：[docs/load-test/queue.md](docs/load-test/queue.md)

---

## Stage 6 Idempotency

送出 `Idempotency-Key` header，重送不會建立第二筆訂單。

```bash
curl -X POST http://localhost:5080/api/flash-sale/1 \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: 550e8400-e29b-41d4-a716-446655440000" \
  -d '{"userId":1,"quantity":1}'
```

三層防護，各自負責不同的失效情境：

```mermaid
flowchart TD
    R(["重送 / 併發重複請求"]) --> L1

    L1["第一層 IdempotencyFilter<br/>Redis SET NX 原子佔用"]
    L1 -->|"已完成 → 回放原本的回應"| OK1["200/202 + Idempotency-Replayed"]
    L1 -->|"處理中 → 併發重複"| C409["409"]
    L1 -.->|"Redis 故障 / 設定關閉"| L2

    L2["第二層 Orders.IdempotencyKey<br/>篩選唯一索引（資料庫強制）"]
    L2 -->|"重複 → Rollback 還原庫存"| C409

    MQ(["RabbitMQ 重複投遞"]) --> L3
    L3["第三層 Worker 去重<br/>同一個唯一索引"]
    L3 -->|"重複 → 視為成功並 ACK"| DONE["不建立第二筆訂單"]

    classDef layer fill:#1971c2,stroke:#1971c2,color:#fff
    classDef good fill:#2f9e44,stroke:#2f9e44,color:#fff
    class L1,L2,L3 layer
    class OK1,DONE good
```

```powershell
.\tests\load\k6\Run-IdempotencyTest.ps1 -Strategy Atomic
.\tests\load\k6\Run-IdempotencyTest.ps1 -Strategy AtomicQueued
```

| 測試 | 請求 | 受理 | 409 | **訂單數** | **庫存** |
|---|---:|---:|---:|---:|---:|
| 依序重送 5 次 | 5 | 1 + 4 回放 | 0 | **1** | **99** |
| 50 個同時重複 | 50 | 1 | 49 | **1** | **99** |
| Worker 重複投遞 3 次 | 3 | — | — | **1** | — |

關閉第一層時**訂單數仍是 1**（資料庫唯一索引守住），
但客戶端只拿得到 409，無法取得原本的訂單編號 —— 這就是第一層的價值。

完整分析與儲存體比較（Redis vs SQL Server）：[docs/idempotency.md](docs/idempotency.md)

---

## Stage 7 Rate Limit

前六個階段都在回答「如何處理更多請求」。這一階段是另一個方向：
**拒絕不合理的請求**，並且用最低的成本拒絕。

兩層並存，任一擋下就是 `429`：

```mermaid
flowchart TD
    R(["請求"]) --> IP

    IP["全域 per-IP<br/>FixedWindow 600 / 60s"]
    IP -->|超額| B1["429 + Retry-After"]
    IP -->|通過| EP

    EP["端點政策 per-User<br/>SlidingWindow 10 / 1s<br/>（僅 /api/flash-sale）"]
    EP -->|超額| B1
    EP -->|通過| C["Controller → DB / Redis"]

    D(["/api/diagnostics"]) -.->|DisableRateLimiting| C

    classDef limiter fill:#1971c2,stroke:#1971c2,color:#fff
    classDef block fill:#c92a2a,stroke:#c92a2a,color:#fff
    class IP,EP limiter
    class B1 block
```

```powershell
.\tests\load\k6\Run-RateLimitTest.ps1 -Label SlidingWindow
```

濫用流量（1000 req/s、同一使用者、10 秒）：

| 演算法 | 請求 | 通過 | 429 | 通過率 |
|---|---:|---:|---:|---:|
| FixedWindow | 10001 | 100 | 9901 | 1.0% |
| **SlidingWindow** | 10001 | **90** | 9911 | **0.9%** |
| TokenBucket | 10000 | 109 | 9891 | 1.1% |
| Concurrency | 10000 | 1544 | 8456 | 15.4% |

正常流量（10 req/s、不同使用者）四種**都是 100% 通過**，不誤傷。

**拒絕的成本：429 平均 0.1 ms，正常處理 12.7–17.0 ms** —— 1/130 以下。
限流器在 Controller 之前，被擋下的請求不碰資料庫或 Redis。

### 固定視窗的邊界爆發

限制「20 次 / 10 秒」，在視窗交界前後各送一批：

| | FixedWindow | SlidingWindow |
|---|---|---|
| 跨越邊界約 3 秒內放行 | **39 個（1.95×）** | **20 個（1.0×）** |

而每個視窗看起來都完全合規。這就是搶購端點選 SlidingWindow 的原因。

> ⚠️ 限流**預設開啟**，會擋下 Stage 2–6 的壓測腳本（單一來源數千請求）。
> 重現先前階段時請先 `$env:RateLimit__Enabled="false"`。

完整分析：[docs/rate-limit.md](docs/rate-limit.md)
