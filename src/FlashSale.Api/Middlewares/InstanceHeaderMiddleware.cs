namespace FlashSale.Api.Middlewares;

/// <summary>
/// 在每個回應加上 <c>X-Instance-Id</c>。
///
/// 多 Instance 之後，「這個請求是哪一台處理的」是一切觀察的前提 ——
/// 看不出負載平衡有沒有生效，就無法判斷任何行為差異是不是分散造成的。
///
/// 來源優先序：環境變數 INSTANCE_ID（docker-compose 指定）→ 機器名稱。
/// </summary>
public class InstanceHeaderMiddleware
{
    public const string HeaderName = "X-Instance-Id";

    private readonly RequestDelegate _next;
    private readonly string _instanceId;

    public InstanceHeaderMiddleware(RequestDelegate next)
    {
        _next = next;
        _instanceId = ResolveInstanceId();
    }

    public static string ResolveInstanceId()
    {
        var configured = Environment.GetEnvironmentVariable("INSTANCE_ID");

        return string.IsNullOrWhiteSpace(configured)
            ? Environment.MachineName
            : configured.Trim();
    }

    public Task InvokeAsync(HttpContext context)
    {
        // 用 OnStarting 而不是直接設定：回應標頭一旦開始送出就不能再改，
        // 而下游中介軟體（例如限流器）可能會提早結束回應。
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = _instanceId;
            return Task.CompletedTask;
        });

        return _next(context);
    }
}
