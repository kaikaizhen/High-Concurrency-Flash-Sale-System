using System.Text.Json;
using FlashSale.Api.Common.Enums;
using FlashSale.Api.Infrastructure.Idempotency;
using FlashSale.Api.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace FlashSale.Api.Filters;

/// <summary>
/// Idempotency-Key 攔截器。
///
///     收到請求
///       │
///       ▼
///     沒帶 Key ──▶ 照常執行（不受保護）
///       │
///       ▼
///     嘗試原子佔用這個 Key
///       │
///       ├── 佔用成功 ──▶ 執行 ──▶ 保存回應 ──▶ 回傳
///       │                  └─ 失敗 ──▶ 釋放佔用（讓使用者可以真的重試）
///       │
///       ├── 已完成   ──▶ 回放先前保存的回應（不再執行）
///       │
///       └── 處理中   ──▶ 409（有另一個相同請求正在處理）
///
/// 為什麼放在 Filter 而不是 Service：
/// 「同一個請求被送了兩次」是 HTTP 傳輸層的問題，不是商業規則。
/// 而且要回放的是**完整的 HTTP 回應**（狀態碼 + 內容），
/// 那是 Controller 邊界才有的東西。
/// </summary>
public class IdempotencyFilter : IAsyncActionFilter
{
    public const string HeaderName = "Idempotency-Key";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IIdempotencyStore _store;
    private readonly IdempotencyOptions _options;
    private readonly ILogger<IdempotencyFilter> _logger;

    public IdempotencyFilter(
        IIdempotencyStore store,
        IOptions<IdempotencyOptions> options,
        ILogger<IdempotencyFilter> logger)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        if (!_options.Enabled)
        {
            await next();
            return;
        }

        var key = ExtractKey(context);

        if (key is null)
        {
            if (_options.Required)
            {
                context.Result = Problem(
                    context,
                    StatusCodes.Status400BadRequest,
                    $"{HeaderName} header is required.");

                return;
            }

            await next();
            return;
        }

        var ttl = TimeSpan.FromSeconds(_options.TtlSeconds);

        var existing = await _store.TryAcquireAsync(key, ttl);

        if (existing is not null)
        {
            HandleDuplicate(context, key, existing);
            return;
        }

        var executed = await next();

        // 例外會由 GlobalExceptionMiddleware 轉成 HTTP 回應，
        // 但那發生在 Filter 之外，這裡看到的是 Exception 而不是 Result。
        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            // 釋放佔用 —— 否則使用者重試會一直收到「處理中」直到 TTL 到期。
            //
            // 注意這裡刻意**不保存**失敗的回應：庫存不足是 409，
            // 但那可能只是這一瞬間的狀態，補貨後同一個 Key 應該能重新嘗試。
            await _store.ReleaseAsync(key);
            return;
        }

        await SaveResponseAsync(key, executed, ttl);
    }

    private static string? ExtractKey(ActionExecutingContext context)
    {
        if (!context.HttpContext.Request.Headers
                .TryGetValue(HeaderName, out var values))
        {
            return null;
        }

        var key = values.ToString();

        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    private void HandleDuplicate(
        ActionExecutingContext context,
        string key,
        IdempotencyEntry existing)
    {
        if (existing.Status == IdempotencyStatus.Completed)
        {
            _logger.LogInformation(
                "Idempotent replay. Key={Key} StatusCode={StatusCode}",
                key,
                existing.StatusCode);

            // 回放原本的回應。客戶端收到的東西與第一次完全相同 ——
            // 這才是冪等的意義：重送不只是「不重複執行」，
            // 還要「拿到跟第一次一樣的答案」。
            context.Result = new ContentResult
            {
                StatusCode = existing.StatusCode,
                ContentType = "application/json",
                Content = existing.ResponseBody
            };

            context.HttpContext.Response.Headers["Idempotency-Replayed"] = "true";

            return;
        }

        // InProgress：有另一個帶相同 Key 的請求正在處理。
        // 回 409 而不是等待 —— 讓客戶端稍後重試，不要佔住連線。
        _logger.LogWarning(
            "Concurrent duplicate request rejected. Key={Key}", key);

        context.Result = Problem(
            context,
            StatusCodes.Status409Conflict,
            "A request with the same Idempotency-Key is currently being processed.");
    }

    private async Task SaveResponseAsync(
        string key,
        ActionExecutedContext executed,
        TimeSpan ttl)
    {
        var statusCode = StatusCodes.Status200OK;
        object? value = null;

        switch (executed.Result)
        {
            case ObjectResult objectResult:
                statusCode = objectResult.StatusCode ?? StatusCodes.Status200OK;
                value = objectResult.Value;
                break;

            case StatusCodeResult statusCodeResult:
                statusCode = statusCodeResult.StatusCode;
                break;

            default:
                // 認不得的 Result 型別無法可靠地回放，
                // 與其存下不完整的內容，不如放棄保護這一次。
                _logger.LogWarning(
                    "Cannot capture response for replay. Key={Key} ResultType={Type}",
                    key,
                    executed.Result?.GetType().Name);

                await _store.ReleaseAsync(key);
                return;
        }

        var body = value is null
            ? null
            : JsonSerializer.Serialize(value, SerializerOptions);

        await _store.CompleteAsync(key, statusCode, body, ttl);
    }

    private static ObjectResult Problem(
        ActionContext context,
        int statusCode,
        string message)
    {
        // 與 GlobalExceptionMiddleware 相同的錯誤格式，
        // 客戶端不需要因為錯誤來源不同而處理兩種格式。
        return new ObjectResult(new
        {
            status = statusCode,
            message,
            traceId = context.HttpContext.TraceIdentifier
        })
        {
            StatusCode = statusCode
        };
    }
}
