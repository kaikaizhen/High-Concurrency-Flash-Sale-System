using AutoMapper;
using FlashSale.Api.Models.ViewModels.Diagnostics;
using FlashSale.Api.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace FlashSale.Api.Controllers;

/// <summary>
/// 壓測用的觀測端點。
///
/// Stage 4 需要精確回答「這 N 個請求打了幾次資料庫、命中率多少」，
/// 壓測腳本會在每一輪開始前 Reset，結束後 Get。
/// </summary>
[ApiController]
[Route("api/diagnostics")]
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
}
