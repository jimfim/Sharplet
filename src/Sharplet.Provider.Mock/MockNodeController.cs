using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Sharplet.Core;

namespace Sharplet.Provider.Mock;

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
            var localIp = Environment.GetEnvironmentVariable("POD_IP") ?? "127.0.0.1";
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
                        new("NoSchedule", "kubernetes.io/sharplet"),
                        new("NoExecute", "kubernetes.io/sharplet")
                    }
                },
                Status = new V1NodeStatus
                {
                    Addresses = new List<V1NodeAddress>
                    {
                        new(localIp, "InternalIP"),
                        new(node.Name(), "Hostname")
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
                        new("True", "Ready"),
                        new("False", "OutOfDisk"),
                        new("False", "MemoryPressure"),
                        new("False", "DiskPressure"),
                        new("False", "NetworkUnavailable"),
                        new("False", "PIDPressure"),
                        new(localIp, "InternalIP"),
                        new(node.Name(), "Hostname")
                    },
                    NodeInfo = new V1NodeSystemInfo("amd64", "", "", "", "", "v1.15.2-vk-N/A", "", "linux", "", ""),
                    DaemonEndpoints = new V1NodeDaemonEndpoints(new V1DaemonEndpoint(10250))
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
        var localIp = Environment.GetEnvironmentVariable("POD_IP") ?? "127.0.0.1";
        return Task.FromResult(new V1NodeStatus
        {
            Addresses = new List<V1NodeAddress>
            {
                new(localIp, "InternalIP"),
                new(nodeName, "Hostname")
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
                new("True", "Ready"),
                new("False", "OutOfDisk"),
                new("False", "MemoryPressure"),
                new("False", "DiskPressure"),
                new("False", "NetworkUnavailable"),
                new("False", "PIDPressure"),
                new(localIp, "InternalIP"),
                new(nodeName, "Hostname")
            },
            NodeInfo = new V1NodeSystemInfo("amd64", "", "", "", "", "v1.15.2-vk-N/A", "", "linux", "", ""),
            DaemonEndpoints = new V1NodeDaemonEndpoints(new V1DaemonEndpoint(10250))
        });
    }
}
