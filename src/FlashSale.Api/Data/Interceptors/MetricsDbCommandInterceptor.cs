using System.Data.Common;
using FlashSale.Api.Infrastructure.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FlashSale.Api.Data.Interceptors;

/// <summary>
/// 計算實際送到資料庫的命令數量。
///
/// Stage 4 需要回答「加了 Redis 之後 DB Query Count 少了多少」，
/// 用讀 log 的方式在 5000 個請求下既不精確也依賴 log level，
/// 因此改用 Interceptor 直接數。
/// </summary>
public class MetricsDbCommandInterceptor : DbCommandInterceptor
{
    private readonly IMetricsCollector _metrics;

    public MetricsDbCommandInterceptor(IMetricsCollector metrics)
    {
        _metrics = metrics;
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        _metrics.RecordDbCommand();
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        _metrics.RecordDbCommand();
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result)
    {
        _metrics.RecordDbCommand();
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        _metrics.RecordDbCommand();
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result)
    {
        _metrics.RecordDbCommand();
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        _metrics.RecordDbCommand();
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }
}
