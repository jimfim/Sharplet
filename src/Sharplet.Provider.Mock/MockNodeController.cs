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
    private bool _loopbackWarned;
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
            string localIp = GetKubeletIp(node.Name());
            DateTime now = DateTime.UtcNow;
            await _kubernetes.CoreV1.CreateNodeAsync(new V1Node
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
                        new V1NodeCondition { Status = "True", Type = "Ready", LastHeartbeatTime = now, LastTransitionTime = now },
                        new V1NodeCondition { Status = "False", Type = "OutOfDisk", LastHeartbeatTime = now, LastTransitionTime = now },
                        new V1NodeCondition { Status = "False", Type = "MemoryPressure", LastHeartbeatTime = now, LastTransitionTime = now },
                        new V1NodeCondition { Status = "False", Type = "DiskPressure", LastHeartbeatTime = now, LastTransitionTime = now },
                        new V1NodeCondition { Status = "False", Type = "NetworkUnavailable", LastHeartbeatTime = now, LastTransitionTime = now },
                        new V1NodeCondition { Status = "False", Type = "PIDPressure", LastHeartbeatTime = now, LastTransitionTime = now }
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
            if (e.Response.StatusCode == HttpStatusCode.Conflict)
            {
                // The node object outlives the process (ttl annotation "0"), so a conflict on
                // create is the expected path on every restart: keep the existing object.
                _logger.LogInformation("node {NodeName} already exists; keeping the existing object", node.Name());
                return;
            }

            _logger.LogError(e, "creating node {NodeName} failed with status code {StatusCode}", node.Name(), e.Response.StatusCode);
            throw;
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
        string localIp = GetKubeletIp(nodeName);
        DateTime now = DateTime.UtcNow;
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
                new V1NodeCondition { Status = "True", Type = "Ready", LastHeartbeatTime = now, LastTransitionTime = now },
                new V1NodeCondition { Status = "False", Type = "OutOfDisk", LastHeartbeatTime = now, LastTransitionTime = now },
                new V1NodeCondition { Status = "False", Type = "MemoryPressure", LastHeartbeatTime = now, LastTransitionTime = now },
                new V1NodeCondition { Status = "False", Type = "DiskPressure", LastHeartbeatTime = now, LastTransitionTime = now },
                new V1NodeCondition { Status = "False", Type = "NetworkUnavailable", LastHeartbeatTime = now, LastTransitionTime = now },
                new V1NodeCondition { Status = "False", Type = "PIDPressure", LastHeartbeatTime = now, LastTransitionTime = now }
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

    // The node's InternalIP is what the API server dials when proxying to the kubelet (pod logs,
    // exec). Outside a pod there is no VKUBELET_POD_IP, and the loopback fallback is unreachable
    // from the cluster: the API server proxies to its own loopback instead (on minikube, the VM's
    // real kubelet), so kubectl logs/k9s come back empty. Point VKUBELET_POD_IP at the IP the
    // cluster uses to reach this machine to enable the proxy. Blank values count as unset: IDE
    // run configurations commonly define the variable with an empty value.
    private string GetKubeletIp(string nodeName)
    {
        string localIp = FirstNonBlank(
                Environment.GetEnvironmentVariable("VKUBELET_POD_IP"),
                Environment.GetEnvironmentVariable("POD_IP"))
            ?? "127.0.0.1";
        if (localIp is "127.0.0.1" && _loopbackWarned is false)
        {
            _loopbackWarned = true;
            _logger.LogWarning(
                "node {NodeName} advertises the loopback address as its InternalIP: the API server cannot proxy requests (pod logs, exec) to this kubelet. " +
                "set VKUBELET_POD_IP to the IP the cluster uses to reach this machine",
                nodeName);
        }
        return localIp;
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (string.IsNullOrWhiteSpace(value) is false)
            {
                return value;
            }
        }

        return null;
    }
}
