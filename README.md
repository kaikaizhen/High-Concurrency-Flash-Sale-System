# High-Concurrency Flash Sale System

以 ASP.NET Core Web API 為主體的高併發學習專案。

學習原則：**先看到問題，再導入技術解決問題。**

完整規劃請見 [docs/計畫.md](docs/計畫.md)，架構規範請見
[docs/architecture/Backend_Architecture_Guideline.md](docs/architecture/Backend_Architecture_Guideline.md)。

---

## 目前進度

| Stage | Branch | 狀態 |
|---|---|---|
| 1. CRUD Baseline | `feature/crud` | 進行中 |
| 2. Race Condition | `feature/race-condition` | 未開始 |
| 3. Concurrency Control | `feature/concurrency-control` | 未開始 |
| 4. Redis | `feature/redis` | 未開始 |
| 5. Message Queue | `feature/message-queue` | 未開始 |
| 6. Idempotency | `feature/idempotency` | 未開始 |
| 7. Rate Limit | `feature/rate-limit` | 未開始 |
| 8. Multi Instance | `feature/multi-instance` | 未開始 |
| 9. Load Test | `feature/load-test` | 未開始 |
| 10. Observability + Optimization | `feature/observability-optimization` | 未開始 |

---

## Git 原則

本專案**只使用本地 Git**，不建立 GitHub Repository、不設定 remote、不執行 push。

```bash
git remote -v   # 預期沒有任何輸出
```

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
