using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Sharplet.Core;

public class MockNodeController : INodeController
{
    private readonly ILogger<MockNodeController> _logger;
    private readonly IKubernetes _kubernetes;

    public MockNodeController(ILogger<MockNodeController> logger, IKubernetes kubernetes)
    {
        _logger = logger;
        _kubernetes = kubernetes;
    }

    public async Task CreateNodeAsync(V1Node node, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("starting node");

        try
        {
            string localIp = Environment.GetEnvironmentVariable("VKUBELET_POD_IP") ?? Environment.GetEnvironmentVariable("POD_IP") ?? "127.0.0.1";
            var response = await _kubernetes.CoreV1.CreateNodeAsync(new V1Node
            {
                Metadata = new V1ObjectMeta
                {
                    Name = node.Name(),
                    Annotations = new Dictionary<string, string>
                    {
                        { "node.alpha.kubernetes.io/ttl", "0" },
                        { "volumes.kubernetes.io/controller-managed-attach-detach", "true" }
                    },
                    Labels = new Dictionary<string, string>
                    {
                        { "alpha.service-controller.kubernetes.io/exclude-balancer", "true" },
                        { "kubernetes.io/role", "agent" },
                        { "node.kubernetes.io/exclude-from-external-load-balancers", "true" },
                        { "type", "virtual-kubelet" },
                        { "kubernetes.io/hostname", node.Name() }
                    }
                },
                Spec = new V1NodeSpec
                {
                    PodCIDR = "10.244.0.0/24",
                    Taints = new List<V1Taint>
                    {
                        new V1Taint { Effect = "NoSchedule", Key = "kubernetes.io/sharplet" },
                        new V1Taint { Effect = "NoExecute", Key = "kubernetes.io/sharplet" }
                    }
                },
                Status = new V1NodeStatus
                {
                    Addresses = new List<V1NodeAddress>
                    {
                        new V1NodeAddress { Address = localIp, Type = "InternalIP" },
                        new V1NodeAddress { Address = node.Name(), Type = "Hostname" }
                    },
                    Allocatable = new Dictionary<string, ResourceQuantity>
                    {
                        { "cpu", new ResourceQuantity("10") },
                        { "memory", new ResourceQuantity("4032800Ki") },
                        { "pods", new ResourceQuantity("5") }
                    },
                    Capacity = new Dictionary<string, ResourceQuantity>
                    {
                        { "cpu", new ResourceQuantity("10") },
                        { "memory", new ResourceQuantity("4032800Ki") },
                        { "pods", new ResourceQuantity("5") }
                    },
                    Conditions = new List<V1NodeCondition>
                    {
                        new V1NodeCondition { Status = "True", Type = "Ready" },
                        new V1NodeCondition { Status = "False", Type = "OutOfDisk" },
                        new V1NodeCondition { Status = "False", Type = "MemoryPressure" },
                        new V1NodeCondition { Status = "False", Type = "DiskPressure" },
                        new V1NodeCondition { Status = "False", Type = "NetworkUnavailable" },
                        new V1NodeCondition { Status = "False", Type = "PIDPressure" }
                    },
                    NodeInfo = new V1NodeSystemInfo
                    {
                        Architecture = "amd64",
                        KubeletVersion = "v1.15.2-vk-N/A",
                        OperatingSystem = "linux"
                    },
                    DaemonEndpoints = new V1NodeDaemonEndpoints { KubeletEndpoint = new V1DaemonEndpoint { Port = 10250 } }
                }
            }, cancellationToken: cancellationToken);
        }
        catch (HttpOperationException e)
        {
            if (e.Response.StatusCode == HttpStatusCode.OK || e.Response.StatusCode == HttpStatusCode.Conflict) return;
        }
    }

    public async Task<V1Node> GetNodeAsync(string nodeName, CancellationToken cancellationToken = default)
    {
        return await _kubernetes.CoreV1.ReadNodeAsync(nodeName, cancellationToken: cancellationToken);
    }

    public async Task DeleteNodeAsync(string nodeName, CancellationToken cancellationToken = default)
    {
        await _kubernetes.CoreV1.DeleteNodeAsync(nodeName, cancellationToken: cancellationToken);
    }

    public Task<V1NodeStatus> GetNodeStatusAsync(string nodeName, CancellationToken cancellationToken = default)
    {
        string localIp = Environment.GetEnvironmentVariable("VKUBELET_POD_IP") ?? Environment.GetEnvironmentVariable("POD_IP") ?? "127.0.0.1";
        return Task.FromResult(new V1NodeStatus
        {
            Addresses = new List<V1NodeAddress>
            {
                new V1NodeAddress { Address = localIp, Type = "InternalIP" },
                new V1NodeAddress { Address = nodeName, Type = "Hostname" }
            },
            Allocatable = new Dictionary<string, ResourceQuantity>
            {
                { "cpu", new ResourceQuantity("10") },
                { "memory", new ResourceQuantity("4032800Ki") },
                { "pods", new ResourceQuantity("5") }
            },
            Capacity = new Dictionary<string, ResourceQuantity>
            {
                { "cpu", new ResourceQuantity("10") },
                { "memory", new ResourceQuantity("4032800Ki") },
                { "pods", new ResourceQuantity("5") }
            },
            Conditions = new List<V1NodeCondition>
            {
                new V1NodeCondition { Status = "True", Type = "Ready" },
                new V1NodeCondition { Status = "False", Type = "OutOfDisk" },
                new V1NodeCondition { Status = "False", Type = "MemoryPressure" },
                new V1NodeCondition { Status = "False", Type = "DiskPressure" },
                new V1NodeCondition { Status = "False", Type = "NetworkUnavailable" },
                new V1NodeCondition { Status = "False", Type = "PIDPressure" }
            },
            NodeInfo = new V1NodeSystemInfo
            {
                Architecture = "amd64",
                KubeletVersion = "v1.15.2-vk-N/A",
                OperatingSystem = "linux"
            },
            DaemonEndpoints = new V1NodeDaemonEndpoints { KubeletEndpoint = new V1DaemonEndpoint { Port = 10250 } }
        });
    }
}
