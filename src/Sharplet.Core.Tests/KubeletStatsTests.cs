using System.Text.Json;
using k8s.Models;
using Sharplet.Core;
using Xunit;

namespace Sharplet.Core.Tests;

/// <summary>
/// The kubelet's <c>/stats/sum</c> endpoint serializes <see cref="StatsSummary"/> (the
/// KubernetesClient package has no models for the <c>stats.k8s.io</c> API group), so the JSON
/// property names and the shape of every nested type are the wire contract the API server and
/// its consumers parse. These tests pin that contract: serialization emits the
/// <c>stats.k8s.io/v1alpha1</c> property names, and a parsed document survives a serialize
/// round trip with its values intact.
/// </summary>
public class KubeletStatsTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StatsSummary_SerializesTheStatsV1Alpha1WireShape()
    {
        // Every optional field is populated so the serialization of each property is observed.
        StatsSummary summary = new()
        {
            ApiVersion = "stats.k8s.io/v1alpha1",
            Kind = "StatsSummary",
            Node = new NodeStats
            {
                Node = "test-node",
                Timestamp = At,
                Cpu = new CpuStats
                {
                    Time = At,
                    UsageNanoCores = 123.5,
                    UsageCoreNanoSeconds = new UsageTime { Time = At, Value = 123456, Average = 1.5 },
                    Average = 2.5,
                },
                Memory = new MemoryStats
                {
                    Time = At,
                    AvailableBytes = 1,
                    UsageBytes = 2,
                    WorkingSetBytes = 3,
                    RssBytes = 4,
                    PageFaults = 5,
                    MajorPageFaults = 6,
                },
                Network = new NetworkStats
                {
                    Time = At,
                    RxBytes = 1,
                    TxBytes = 2,
                    RxErrors = 3,
                    TxErrors = 4,
                },
            },
            Pods =
            [
                new PodStats
                {
                    PodRef = new PodReference
                    {
                        Reference = new V1ObjectMeta { Name = "web", NamespaceProperty = "default" },
                        Timestamp = At,
                    },
                    Cpu = new CpuStats { Time = At, UsageNanoCores = 0 },
                    Memory = new MemoryStats { Time = At },
                    Network = new NetworkStats { Time = At },
                    Containers =
                    [
                        new ContainerStats
                        {
                            Name = "web-container",
                            StartTime = new DateTimeOffset(2026, 9, 15, 11, 0, 0, TimeSpan.Zero),
                            Cpu = new CpuStats { Time = At },
                            Memory = new MemoryStats { Time = At },
                            Rootfs = new FilesystemStats
                            {
                                Time = At,
                                Device = "rootfs",
                                Fstype = "overlay",
                                CapacityBytes = 100,
                                UsedBytes = 20,
                                AvailBytes = 80,
                                Inodes = 1000,
                                InodesUsed = 100,
                            },
                            Logs = new FilesystemStats { Time = At, Device = "logs" },
                        },
                    ],
                },
            ],
        };

        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        JsonElement root = document.RootElement;

        Assert.Equal("stats.k8s.io/v1alpha1", root.GetProperty("apiVersion").GetString());
        Assert.Equal("StatsSummary", root.GetProperty("kind").GetString());

        JsonElement node = root.GetProperty("node");
        Assert.Equal("test-node", node.GetProperty("node").GetString());
        JsonElement nodeCpu = node.GetProperty("cpu");
        Assert.Equal(123.5, nodeCpu.GetProperty("usageNanoCores").GetDouble());
        Assert.Equal(123456, nodeCpu.GetProperty("usageCoreNanoSeconds").GetProperty("value").GetInt64());
        Assert.Equal(1.5, nodeCpu.GetProperty("usageCoreNanoSeconds").GetProperty("average").GetDouble());
        Assert.Equal(2.5, nodeCpu.GetProperty("average").GetDouble());
        JsonElement nodeMemory = node.GetProperty("memory");
        Assert.Equal(1, nodeMemory.GetProperty("availableBytes").GetInt64());
        Assert.Equal(2, nodeMemory.GetProperty("usageBytes").GetInt64());
        Assert.Equal(3, nodeMemory.GetProperty("workingSetBytes").GetInt64());
        Assert.Equal(4, nodeMemory.GetProperty("rssBytes").GetInt64());
        Assert.Equal(5, nodeMemory.GetProperty("pageFaults").GetInt64());
        Assert.Equal(6, nodeMemory.GetProperty("majorPageFaults").GetInt64());
        JsonElement nodeNetwork = node.GetProperty("network");
        Assert.Equal(1, nodeNetwork.GetProperty("rxBytes").GetInt64());
        Assert.Equal(2, nodeNetwork.GetProperty("txBytes").GetInt64());
        Assert.Equal(3, nodeNetwork.GetProperty("rxErrors").GetInt64());
        Assert.Equal(4, nodeNetwork.GetProperty("txErrors").GetInt64());

        JsonElement pod = root.GetProperty("pods")[0];
        Assert.Equal("web", pod.GetProperty("podRef").GetProperty("reference").GetProperty("name").GetString());
        Assert.Equal("default", pod.GetProperty("podRef").GetProperty("reference").GetProperty("namespace").GetString());
        Assert.Equal(0, pod.GetProperty("cpu").GetProperty("usageNanoCores").GetDouble());
        Assert.Equal("web-container", pod.GetProperty("containers")[0].GetProperty("name").GetString());
        JsonElement container = pod.GetProperty("containers")[0];
        Assert.Equal(At.AddHours(-1), container.GetProperty("startTime").GetDateTimeOffset());
        JsonElement rootfs = container.GetProperty("rootfs");
        Assert.Equal("rootfs", rootfs.GetProperty("device").GetString());
        Assert.Equal("overlay", rootfs.GetProperty("fstype").GetString());
        Assert.Equal(100, rootfs.GetProperty("capacityBytes").GetInt64());
        Assert.Equal(20, rootfs.GetProperty("usedBytes").GetInt64());
        Assert.Equal(80, rootfs.GetProperty("availBytes").GetInt64());
        Assert.Equal(1000, rootfs.GetProperty("inodes").GetInt64());
        Assert.Equal(100, rootfs.GetProperty("inodesUsed").GetInt64());
        Assert.Equal("logs", container.GetProperty("logs").GetProperty("device").GetString());
    }

    [Fact]
    public void StatsSummary_ParsesAPartialDocumentAndRoundTripsItsValues()
    {
        // A provider measuring real usage may emit a document with optional stat fields
        // omitted; whatever parses must survive a serialize -> parse round trip unchanged.
        string json = """
            {
              "apiVersion": "stats.k8s.io/v1alpha1",
              "kind": "StatsSummary",
              "node": {
                "node": "test-node",
                "timestamp": "2026-09-15T12:00:00Z",
                "cpu": {
                  "time": "2026-09-15T12:00:00Z",
                  "usageNanoCores": 42,
                  "usageCoreNanoSeconds": { "time": "2026-09-15T12:00:00Z", "value": 7, "average": 0.5 }
                },
                "memory": {
                  "time": "2026-09-15T12:00:00Z",
                  "availableBytes": 1,
                  "usageBytes": 2,
                  "workingSetBytes": 3,
                  "rssBytes": 4,
                  "pageFaults": 5,
                  "majorPageFaults": 6
                },
                "network": {
                  "time": "2026-09-15T12:00:00Z",
                  "rxBytes": 1,
                  "txBytes": 2,
                  "rxErrors": 3,
                  "txErrors": 4
                }
              },
              "pods": [
                {
                  "podRef": {
                    "reference": { "name": "web", "namespace": "default" },
                    "timestamp": "2026-09-15T12:00:00Z"
                  },
                  "cpu": { "time": "2026-09-15T12:00:00Z", "usageNanoCores": 0 },
                  "memory": { "time": "2026-09-15T12:00:00Z" },
                  "network": { "time": "2026-09-15T12:00:00Z" },
                  "containers": [
                    {
                      "name": "web-container",
                      "startTime": "2026-09-15T11:00:00Z",
                      "cpu": { "time": "2026-09-15T12:00:00Z" },
                      "memory": { "time": "2026-09-15T12:00:00Z" },
                      "rootfs": {
                        "time": "2026-09-15T12:00:00Z",
                        "device": "rootfs",
                        "fstype": "overlay",
                        "capacityBytes": 100,
                        "usedBytes": 20,
                        "availBytes": 80,
                        "inodes": 1000,
                        "inodesUsed": 100
                      },
                      "logs": { "time": "2026-09-15T12:00:00Z", "device": "logs" }
                    }
                  ]
                }
              ]
            }
            """;

        StatsSummary summary = JsonSerializer.Deserialize<StatsSummary>(json)!;

        Assert.Equal("test-node", summary.Node.Node);
        Assert.Equal(42, summary.Node.Cpu.UsageNanoCores);
        Assert.Equal(7, summary.Node.Cpu.UsageCoreNanoSeconds.Value);
        Assert.Equal(0.5, summary.Node.Cpu.UsageCoreNanoSeconds.Average);
        Assert.Equal(1, summary.Node.Memory.AvailableBytes);
        Assert.Equal(6, summary.Node.Memory.MajorPageFaults);
        Assert.Equal(4, summary.Node.Network.TxErrors);
        PodStats pod = Assert.Single(summary.Pods);
        Assert.Equal("web", pod.PodRef.Reference.Name);
        Assert.Equal("default", pod.PodRef.Reference.NamespaceProperty);
        // Optional fields absent from the document keep their null/zero defaults.
        Assert.Null(pod.Cpu.Average);
        Assert.Null(pod.Memory.AvailableBytes);
        Assert.Equal(0, pod.Memory.UsageBytes);
        Assert.Equal(0, pod.Network.RxErrors);
        ContainerStats container = Assert.Single(pod.Containers);
        Assert.Equal(At.AddHours(-1), container.StartTime);
        Assert.Equal("overlay", container.Rootfs!.Fstype);
        Assert.Equal(1000, container.Rootfs.Inodes);
        Assert.Equal("logs", container.Logs!.Device);
        Assert.Null(container.Logs.Fstype);

        string reserialized = JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        StatsSummary reparsed = JsonSerializer.Deserialize<StatsSummary>(reserialized)!;
        Assert.Equal(42, reparsed.Node.Cpu.UsageNanoCores);
        Assert.Equal(0.5, reparsed.Node.Cpu.UsageCoreNanoSeconds.Average);
        PodStats reparsedPod = Assert.Single(reparsed.Pods);
        Assert.Equal("web", reparsedPod.PodRef.Reference.Name);
        Assert.Equal(1000, Assert.Single(reparsedPod.Containers).Rootfs!.Inodes);
        Assert.Equal(At.AddHours(-1), reparsedPod.Containers[0].StartTime);
    }
}