using System.Diagnostics;
using FlashSale.Api.Data;
using FlashSale.Api.Infrastructure.Messaging;
using FlashSale.Api.Middlewares;
using FlashSale.Api.Models.Dtos.Diagnostics;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace FlashSale.Api.Infrastructure.Diagnostics;

public class SystemMetricsProvider : ISystemMetricsProvider
{
    /// <summary>
    /// CPU 使用率必須用「兩次取樣的差」計算 —— 單次快照只知道
    /// 行程從啟動到現在累計用了多少 CPU 時間，那是平均值不是當下的負載。
    ///
    /// 因為要跨請求保留上一次的取樣，這個服務必須是 Singleton。
    /// </summary>
    private readonly object _cpuSampleLock = new();

    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSampleAt;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectionMultiplexer _redis;
    private readonly IQueueInspector _queueInspector;
    private readonly ILogger<SystemMetricsProvider> _logger;

    public SystemMetricsProvider(
        IServiceScopeFactory scopeFactory,
        IConnectionMultiplexer redis,
        IQueueInspector queueInspector,
        ILogger<SystemMetricsProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _redis = redis;
        _queueInspector = queueInspector;
        _logger = logger;

        var process = Process.GetCurrentProcess();
        _lastCpuTime = process.TotalProcessorTime;
        _lastCpuSampleAt = DateTime.UtcNow;
    }

    public async Task<SystemMetricsDtoModel> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var metrics = new SystemMetricsDtoModel
        {
            InstanceId = InstanceHeaderMiddleware.ResolveInstanceId(),
            Process = ReadProcessMetrics()
        };

        // 三個相依各自獨立量測，任一失敗不影響其他。
        metrics.Database = await MeasureDatabaseAsync(cancellationToken);
        metrics.Redis = await MeasureRedisAsync();
        metrics.Queue = await _queueInspector.GetQueueMetricsAsync(cancellationToken);

        return metrics;
    }

    private SystemMetricsDtoModel.ProcessMetrics ReadProcessMetrics()
    {
        var process = Process.GetCurrentProcess();
        var processorCount = Environment.ProcessorCount;

        double cpuPercent;

        lock (_cpuSampleLock)
        {
            var now = DateTime.UtcNow;
            var cpuTime = process.TotalProcessorTime;

            var wallElapsed = (now - _lastCpuSampleAt).TotalMilliseconds;
            var cpuElapsed = (cpuTime - _lastCpuTime).TotalMilliseconds;

            // 除以處理器數，讓「100%」代表整台機器滿載而不是單核滿載。
            cpuPercent = wallElapsed > 0
                ? Math.Round(cpuElapsed / (wallElapsed * processorCount) * 100, 1)
                : 0;

            _lastCpuTime = cpuTime;
            _lastCpuSampleAt = now;
        }

        return new SystemMetricsDtoModel.ProcessMetrics
        {
            CpuPercent = cpuPercent,
            WorkingSetMb = Math.Round(process.WorkingSet64 / 1024d / 1024d, 1),
            GcHeapMb = Math.Round(GC.GetTotalMemory(forceFullCollection: false)
                                  / 1024d / 1024d, 1),
            ThreadCount = process.Threads.Count,
            ProcessorCount = processorCount
        };
    }

    /// <summary>
    /// 資料庫延遲與連線數。
    ///
    /// 連線數查的是 SQL Server 端「目前這個資料庫有幾個 session」。
    ///
    /// **需要 VIEW SERVER STATE 權限。** 沒有的話 sys.dm_exec_sessions
    /// 只會回傳呼叫者自己的那一條 session，數字永遠是 1。
    /// 那比不回報更糟 —— 看到「連線數 1」會讓人以為連線池很閒，
    /// 實際上可能已經耗盡。因此這裡明確偵測並回報 -1（不可用）。
    ///
    /// 要啟用：<c>GRANT VIEW SERVER STATE TO [使用者]</c>
    /// </summary>
    private async Task<SystemMetricsDtoModel.DependencyMetrics> MeasureDatabaseAsync(
        CancellationToken cancellationToken)
    {
        var result = new SystemMetricsDtoModel.DependencyMetrics();

        try
        {
            using var scope = _scopeFactory.CreateScope();

            var dbContext = scope.ServiceProvider
                .GetRequiredService<AppDbContext>();

            var stopwatch = Stopwatch.StartNew();

            var row = await dbContext.Database
                .SqlQuery<DbProbeRow>($@"
                    SELECT
                        CAST(HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE') AS int)
                            AS CanSeeAllSessions,
                        (SELECT COUNT(*)
                         FROM sys.dm_exec_sessions
                         WHERE database_id = DB_ID()) AS SessionCount")
                .FirstAsync(cancellationToken);

            stopwatch.Stop();

            result.LatencyMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2);
            result.Available = true;

            // 沒有權限時看到的只有自己，回報 -1 表示「量不到」而不是「只有 1 條」。
            result.Connections = row.CanSeeAllSessions == 1
                ? row.SessionCount
                : -1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to measure database metrics.");
        }

        return result;
    }

    private sealed class DbProbeRow
    {
        public int CanSeeAllSessions { get; set; }

        public int SessionCount { get; set; }
    }

    private async Task<SystemMetricsDtoModel.DependencyMetrics> MeasureRedisAsync()
    {
        var result = new SystemMetricsDtoModel.DependencyMetrics();

        try
        {
            var latency = await _redis.GetDatabase().PingAsync();

            result.LatencyMs = Math.Round(latency.TotalMilliseconds, 2);
            result.Available = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to measure Redis metrics.");
        }

        return result;
    }
}
