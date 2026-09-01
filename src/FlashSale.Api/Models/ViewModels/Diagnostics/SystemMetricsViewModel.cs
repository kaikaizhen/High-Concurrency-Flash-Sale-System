namespace FlashSale.Api.Models.ViewModels.Diagnostics;

public class SystemMetricsViewModel
{
    public string InstanceId { get; set; } = string.Empty;

    public ProcessViewModel Process { get; set; } = new();

    public DependencyViewModel Database { get; set; } = new();

    public DependencyViewModel Redis { get; set; } = new();

    public QueueMetricsViewModel Queue { get; set; } = new();

    public class ProcessViewModel
    {
        public double CpuPercent { get; set; }

        public double WorkingSetMb { get; set; }

        public double GcHeapMb { get; set; }

        public int ThreadCount { get; set; }

        public int ProcessorCount { get; set; }
    }

    public class DependencyViewModel
    {
        public double LatencyMs { get; set; }

        public int Connections { get; set; }

        public bool Available { get; set; }
    }
}
