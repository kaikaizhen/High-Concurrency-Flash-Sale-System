using System.Text.Json;
using FlashSale.Api.Common.Enums;
using FlashSale.Api.Filters;
using FlashSale.Api.Infrastructure.Idempotency;
using FlashSale.Api.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FlashSale.UnitTests.Filters;

public class IdempotencyFilterTests
{
    private readonly Mock<IIdempotencyStore> _store = new();

    private readonly IdempotencyOptions _options = new()
    {
        Enabled = true,
        TtlSeconds = 3600,
        Required = false
    };

    private IdempotencyFilter CreateSut()
    {
        return new IdempotencyFilter(
            _store.Object,
            Microsoft.Extensions.Options.Options.Create(_options),
            NullLogger<IdempotencyFilter>.Instance);
    }

    private static ActionExecutingContext CreateContext(string? key)
    {
        var httpContext = new DefaultHttpContext();

        if (key is not null)
        {
            httpContext.Request.Headers[IdempotencyFilter.HeaderName] = key;
        }

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());

        return new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: null!);
    }

    private static ActionExecutionDelegate Next(
        IActionResult? result = null,
        Exception? exception = null)
    {
        return () =>
        {
            var executed = new ActionExecutedContext(
                new ActionContext(
                    new DefaultHttpContext(),
                    new RouteData(),
                    new ActionDescriptor()),
                new List<IFilterMetadata>(),
                controller: null!)
            {
                Result = result ?? new OkObjectResult(new { id = 1 }),
                Exception = exception
            };

            return Task.FromResult(executed);
        };
    }

    // ------------------------------------------------------------------
    // 沒帶 Key
    // ------------------------------------------------------------------

    [Fact]
    public async Task WhenNoKey_ShouldExecuteWithoutProtection()
    {
        var context = CreateContext(key: null);
        var executed = false;

        await CreateSut().OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Next()();
        });

        Assert.True(executed);

        _store.Verify(
            x => x.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenNoKeyAndRequired_ShouldReturn400()
    {
        _options.Required = true;

        var context = CreateContext(key: null);
        var executed = false;

        await CreateSut().OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Next()();
        });

        Assert.False(executed);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
    }

    [Fact]
    public async Task WhenDisabled_ShouldSkipEntirely()
    {
        _options.Enabled = false;

        var context = CreateContext("key-1");

        await CreateSut().OnActionExecutionAsync(context, Next());

        _store.Verify(
            x => x.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()),
            Times.Never);
    }

    // ------------------------------------------------------------------
    // 首次請求
    // ------------------------------------------------------------------

    [Fact]
    public async Task WhenKeyIsNew_ShouldExecuteAndSaveResponse()
    {
        _store
            .Setup(x => x.TryAcquireAsync("key-1", It.IsAny<TimeSpan>()))
            .ReturnsAsync((IdempotencyEntry?)null);

        var context = CreateContext("key-1");

        var payload = new { status = "Completed", requestId = "abc" };

        await CreateSut().OnActionExecutionAsync(
            context,
            Next(new ObjectResult(payload) { StatusCode = 200 }));

        _store.Verify(
            x => x.CompleteAsync(
                "key-1",
                200,
                It.Is<string>(body =>
                    body!.Contains("Completed") && body.Contains("abc")),
                It.IsAny<TimeSpan>()),
            Times.Once);
    }

    // ------------------------------------------------------------------
    // 重送：計畫 §11 的核心情境
    // ------------------------------------------------------------------

    [Fact]
    public async Task WhenKeyAlreadyCompleted_ShouldReplayWithoutExecuting()
    {
        var savedBody = JsonSerializer.Serialize(
            new { status = "Completed", requestId = "abc" });

        _store
            .Setup(x => x.TryAcquireAsync("key-1", It.IsAny<TimeSpan>()))
            .ReturnsAsync(new IdempotencyEntry
            {
                Status = IdempotencyStatus.Completed,
                StatusCode = 200,
                ResponseBody = savedBody
            });

        var context = CreateContext("key-1");
        var executed = false;

        await CreateSut().OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Next()();
        });

        // 關鍵：業務邏輯完全沒有被執行 —— 不會建立第二筆訂單
        Assert.False(executed);

        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(200, result.StatusCode);

        // 而且回放的內容與第一次完全相同。
        // 冪等不只是「不重複執行」，還要「拿到跟第一次一樣的答案」。
        Assert.Equal(savedBody, result.Content);

        Assert.Equal(
            "true",
            context.HttpContext.Response.Headers["Idempotency-Replayed"]);
    }

    [Fact]
    public async Task WhenKeyIsInProgress_ShouldReturn409WithoutExecuting()
    {
        _store
            .Setup(x => x.TryAcquireAsync("key-1", It.IsAny<TimeSpan>()))
            .ReturnsAsync(new IdempotencyEntry
            {
                Status = IdempotencyStatus.InProgress
            });

        var context = CreateContext("key-1");
        var executed = false;

        await CreateSut().OnActionExecutionAsync(context, () =>
        {
            executed = true;
            return Next()();
        });

        // 併發重複：另一個相同 Key 的請求正在處理中。
        // 這是 InProgress 狀態存在的唯一理由。
        Assert.False(executed);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
    }

    // ------------------------------------------------------------------
    // 失敗處理
    // ------------------------------------------------------------------

    [Fact]
    public async Task WhenActionThrows_ShouldReleaseKeySoUserCanRetry()
    {
        _store
            .Setup(x => x.TryAcquireAsync("key-1", It.IsAny<TimeSpan>()))
            .ReturnsAsync((IdempotencyEntry?)null);

        var context = CreateContext("key-1");

        await CreateSut().OnActionExecutionAsync(
            context,
            Next(exception: new InvalidOperationException("boom")));

        // 不釋放的話，這個 Key 會卡在 InProgress 直到 TTL 到期，
        // 使用者重試只會一直收到「處理中」而無法真正重試。
        _store.Verify(x => x.ReleaseAsync("key-1"), Times.Once);

        _store.Verify(
            x => x.CompleteAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>()),
            Times.Never);
    }

    [Fact]
    public async Task WhenKeyHasSurroundingWhitespace_ShouldTrim()
    {
        _store
            .Setup(x => x.TryAcquireAsync("key-1", It.IsAny<TimeSpan>()))
            .ReturnsAsync((IdempotencyEntry?)null);

        var context = CreateContext("  key-1  ");

        await CreateSut().OnActionExecutionAsync(context, Next());

        // 沒有 Trim 的話，"key-1" 與 " key-1" 會被當成兩個不同的請求
        _store.Verify(
            x => x.TryAcquireAsync("key-1", It.IsAny<TimeSpan>()),
            Times.Once);
    }
}
