using System.ComponentModel.DataAnnotations;
using FlashSale.Api.Common.Enums;

namespace FlashSale.Api.Models.Params.FlashSales;

public class CreateFlashSaleParamModel
{
    [Range(1, int.MaxValue)]
    public int UserId { get; set; }

    [Range(1, int.MaxValue)]
    public int Quantity { get; set; } = 1;

    /// <summary>
    /// 併發控制策略。
    ///
    /// Stage 3 為了在同一次測試中比較四種做法而開放指定。
    /// 未指定時使用專案選定的主要方案（Atomic）。
    /// </summary>
    [EnumDataType(typeof(FlashSaleStrategy))]
    public FlashSaleStrategy Strategy { get; set; } = FlashSaleStrategy.Atomic;
}
