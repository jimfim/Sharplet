using System.Text.Json.Serialization;
using k8s.Models;

namespace Sharplet.Core;

/// <summary>
/// Wire shape of the kubelet's <c>/stats/sum</c> endpoint (<c>stats.k8s.io/v1alpha1</c>). The
/// KubernetesClient package does not generate models for the <c>stats.k8s.io</c> API group, so the
/// endpoint serializes this internal shape instead. The reference implementation reports zero
/// resource usage for every pod the provider knows about; a provider that measures real usage
/// should map its own <c>/stats/sum</c> instead of relying on this one.
/// </summary>
internal sealed class StatsSummary
{
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; set; } = "stats.k8s.io/v1alpha1";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "StatsSummary";

    [JsonPropertyName("node")]
    public required NodeStats Node { get; set; }

    [JsonPropertyName("pods")]
    public List<PodStats> Pods { get; set; } = new();
}

internal sealed class NodeStats
{
    [JsonPropertyName("node")]
    public required string Node { get; set; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("cpu")]
    public required CpuStats Cpu { get; set; }

    [JsonPropertyName("memory")]
    public required MemoryStats Memory { get; set; }

    [JsonPropertyName("network")]
    public required NetworkStats Network { get; set; }
}

internal sealed class PodStats
{
    [JsonPropertyName("podRef")]
    public required PodReference PodRef { get; set; }

    [JsonPropertyName("cpu")]
    public required CpuStats Cpu { get; set; }

    [JsonPropertyName("memory")]
    public required MemoryStats Memory { get; set; }

    [JsonPropertyName("network")]
    public required NetworkStats Network { get; set; }

    [JsonPropertyName("containers")]
    public List<ContainerStats> Containers { get; set; } = new();
}

internal sealed class PodReference
{
    [JsonPropertyName("reference")]
    public required V1ObjectMeta Reference { get; set; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; set; }
}

internal sealed class ContainerStats
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("startTime")]
    public DateTimeOffset? StartTime { get; set; }

    [JsonPropertyName("cpu")]
    public required CpuStats Cpu { get; set; }

    [JsonPropertyName("memory")]
    public required MemoryStats Memory { get; set; }

    [JsonPropertyName("rootfs")]
    public FilesystemStats? Rootfs { get; set; }

    [JsonPropertyName("logs")]
    public FilesystemStats? Logs { get; set; }
}

internal sealed class CpuStats
{
    [JsonPropertyName("time")]
    public required DateTimeOffset Time { get; set; }

    [JsonPropertyName("usageNanoCores")]
    public double? UsageNanoCores { get; set; } = 0;

    [JsonPropertyName("usageCoreNanoSeconds")]
    public UsageTime UsageCoreNanoSeconds { get; set; } = new();

    [JsonPropertyName("average")]
    public double? Average { get; set; }
}

internal sealed class UsageTime
{
    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; set; }

    [JsonPropertyName("value")]
    public long? Value { get; set; } = 0;

    [JsonPropertyName("average")]
    public double? Average { get; set; }
}

internal sealed class MemoryStats
{
    [JsonPropertyName("time")]
    public required DateTimeOffset Time { get; set; }

    [JsonPropertyName("availableBytes")]
    public long? AvailableBytes { get; set; }

    [JsonPropertyName("usageBytes")]
    public long? UsageBytes { get; set; } = 0;

    [JsonPropertyName("workingSetBytes")]
    public long? WorkingSetBytes { get; set; } = 0;

    [JsonPropertyName("rssBytes")]
    public long? RssBytes { get; set; } = 0;

    [JsonPropertyName("pageFaults")]
    public long? PageFaults { get; set; } = 0;

    [JsonPropertyName("majorPageFaults")]
    public long? MajorPageFaults { get; set; } = 0;
}

internal sealed class NetworkStats
{
    [JsonPropertyName("time")]
    public required DateTimeOffset Time { get; set; }

    [JsonPropertyName("rxBytes")]
    public long? RxBytes { get; set; } = 0;

    [JsonPropertyName("txBytes")]
    public long? TxBytes { get; set; } = 0;

    [JsonPropertyName("rxErrors")]
    public long? RxErrors { get; set; } = 0;

    [JsonPropertyName("txErrors")]
    public long? TxErrors { get; set; } = 0;
}

internal sealed class FilesystemStats
{
    [JsonPropertyName("time")]
    public required DateTimeOffset Time { get; set; }

    [JsonPropertyName("device")]
    public required string Device { get; set; }

    [JsonPropertyName("fstype")]
    public string? Fstype { get; set; }

    [JsonPropertyName("capacityBytes")]
    public long? CapacityBytes { get; set; } = 0;

    [JsonPropertyName("usedBytes")]
    public long? UsedBytes { get; set; } = 0;

    [JsonPropertyName("availBytes")]
    public long? AvailBytes { get; set; } = 0;

    [JsonPropertyName("inodes")]
    public long? Inodes { get; set; } = 0;

    [JsonPropertyName("inodesUsed")]
    public long? InodesUsed { get; set; } = 0;
}