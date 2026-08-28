using System.Net;
using System.Text.Json;
using FlashSale.Api.Common.Exceptions;

namespace FlashSale.Api.Middlewares;

/// <summary>
/// 將 Service 丟出的商業 Exception 統一轉換為 HTTP Response。
/// Controller 因此不需要撰寫重複的 try/catch。
/// </summary>
public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (NotFoundException ex)
        {
            await WriteAsync(
                context,
                HttpStatusCode.NotFound,
                ex.Message);
        }
        catch (BusinessException ex)
        {
            await WriteAsync(
                context,
                HttpStatusCode.Conflict,
                ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception.");

            await WriteAsync(
                context,
                HttpStatusCode.InternalServerError,
                "Unexpected error.");
        }
    }

    private static async Task WriteAsync(
        HttpContext context,
        HttpStatusCode statusCode,
        string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";

        var payload = JsonSerializer.Serialize(new
        {
            status = (int)statusCode,
            message,
            traceId = context.TraceIdentifier
        });

        await context.Response.WriteAsync(payload);
    }
}
