using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Sharplet.Core;

public interface INodeController
{
    Task RegisterNodeAsync(string nodeName);

    Task<V1NodeStatus> GetNodeStatusAsync(string nodeName);
    Task RemoveNodeAsync(string nodeName);
}

internal class NodeController : INodeController
{
    private readonly IKubernetes _kubernetes;
    private readonly ILogger<NodeController> _logger;

    public NodeController(IKubernetes kubernetes, ILogger<NodeController> logger)
    {
        _kubernetes = kubernetes;
        _logger = logger;
    }

    public async Task RegisterNodeAsync(string nodeName)
    {
        _logger.LogInformation("starting node");

        try
        {
            var localIp = Environment.GetEnvironmentVariable("VKUBELET_POD_IP");
            var response = await _kubernetes.CoreV1.CreateNodeWithHttpMessagesAsync(new V1Node
            {
                Metadata = new V1ObjectMeta
                {
                    Name = nodeName,
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
                        { "kubernetes.io/hostname", nodeName }
                    }
                },
                Spec = new V1NodeSpec
                {
                    PodCIDR = "10.244.0.0/24",
                    Taints = new List<V1Taint>
                    {
                        new("NoSchedule", "kubernetes.io/sharpkube"),
                        new("NoExecute", "kubernetes.io/sharpkube")
                    }
                },
                Status = new V1NodeStatus
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
                }
            });

            if (response.Response.IsSuccessStatusCode)
                _logger.LogInformation("node start successfully");
            else
                _logger.LogError("Node not started {@Reason}", response.Body);
        }
        catch (HttpOperationException e)
        {
            if (e.Response.StatusCode == HttpStatusCode.OK || e.Response.StatusCode == HttpStatusCode.Conflict) return;
        }
    }

    public Task<V1NodeStatus> GetNodeStatusAsync(string nodeName)
    {
        var localIp = Environment.GetEnvironmentVariable("VKUBELET_POD_IP");
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

    public async Task RemoveNodeAsync(string nodeName)
    {
        _logger.LogInformation("Deleting node");
        await _kubernetes.CoreV1.DeleteNodeWithHttpMessagesAsync(nodeName, gracePeriodSeconds: 0);
        _logger.LogInformation("node Deleted");
    }
}