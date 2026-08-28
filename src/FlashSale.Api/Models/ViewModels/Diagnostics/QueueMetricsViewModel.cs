namespace FlashSale.Api.Models.ViewModels.Diagnostics;

public class QueueMetricsViewModel
{
    public uint PendingOrders { get; set; }

    public uint PendingRetries { get; set; }

    public uint DeadLettered { get; set; }

    public bool Available { get; set; }
}
