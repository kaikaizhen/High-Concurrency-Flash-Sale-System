using AutoMapper;
using FlashSale.Api.Models.ViewModels.Diagnostics;
using FlashSale.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FlashSale.Api.Controllers;

/// <summary>
/// 壓測用的觀測端點。
///
/// Stage 4 需要精確回答「這 N 個請求打了幾次資料庫、命中率多少」，
/// 壓測腳本會在每一輪開始前 Reset，結束後 Get。
/// </summary>
[ApiController]
[Route("api/diagnostics")]
// 觀測端點豁免限流：壓測腳本會高頻輪詢它（例如 Stage 5 每秒取樣佇列長度）。
// 讓觀測工具被自己要觀測的限流機制擋下，量到的就不是系統的真實狀態了。
[DisableRateLimiting]
public class DiagnosticsController : ControllerBase
{
    private readonly IDiagnosticsService _diagnosticsService;
    private readonly IMapper _mapper;

    public DiagnosticsController(
        IDiagnosticsService diagnosticsService,
        IMapper mapper)
    {
        _diagnosticsService = diagnosticsService;
        _mapper = mapper;
    }

    [HttpGet("metrics")]
    public ActionResult<MetricsViewModel> GetMetrics()
    {
        var result = _diagnosticsService.GetMetrics();

        return Ok(_mapper.Map<MetricsViewModel>(result));
    }

    [HttpPost("metrics/reset")]
    public ActionResult ResetMetrics()
    {
        _diagnosticsService.ResetMetrics();

        return NoContent();
    }

    /// <summary>
    /// Stage 5：佇列待處理訊息數。用來觀察削峰填谷 ——
    /// 流量尖峰時這個數字會急速上升，之後由 Worker 以固定速度消化。
    /// </summary>
    [HttpGet("queue")]
    public async Task<ActionResult<QueueMetricsViewModel>> GetQueueMetricsAsync()
    {
        var result = await _diagnosticsService.GetQueueMetricsAsync();

        return Ok(_mapper.Map<QueueMetricsViewModel>(result));
    }

    /// <summary>
    /// Stage 9：系統面指標（CPU / 記憶體 / DB 連線 / Redis 延遲 / 佇列）。
    ///
    /// 光看 RPS 與 P99 只知道「慢」，不知道慢在哪裡。
    /// 壓測腳本會在每個階段取樣這個端點來定位瓶頸。
    /// </summary>
    [HttpGet("system")]
    public async Task<ActionResult<SystemMetricsViewModel>> GetSystemMetricsAsync()
    {
        var result = await _diagnosticsService.GetSystemMetricsAsync();

        return Ok(_mapper.Map<SystemMetricsViewModel>(result));
    }
}
