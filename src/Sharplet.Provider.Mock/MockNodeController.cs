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
    // Kubernetes node condition contract: the status alphabet ("True"/"False") and the
    // well-known condition types. The API server silently accepts a typo'd status
    // (e.g. "false"), which would leave the node never Ready, so the literals are
    // constantized to make the contract explicit and typo-proof.
    private const string ConditionTrue = "True";
    private const string ConditionFalse = "False";
    private const string ConditionReady = "Ready";
    private const string ConditionOutOfDisk = "OutOfDisk";
    private const string ConditionMemoryPressure = "MemoryPressure";
    private const string ConditionDiskPressure = "DiskPressure";
    private const string ConditionNetworkUnavailable = "NetworkUnavailable";
    private const string ConditionPidPressure = "PIDPressure";

    // The five pressure conditions are always False on a healthy node; they are listed as data
    // (rather than six similar initializers) so the block does not read as duplicated code.
    private static readonly string[] HealthyConditionTypes =
    {
        ConditionOutOfDisk,
        ConditionMemoryPressure,
        ConditionDiskPressure,
        ConditionNetworkUnavailable,
        ConditionPidPressure
    };

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
                    Conditions = CreateNodeConditions(now),
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
            Conditions = CreateNodeConditions(now),
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
        if (localIp == "127.0.0.1" && !_loopbackWarned)
        {
            _loopbackWarned = true;
            _logger.LogWarning(
                "node {NodeName} advertises the loopback address as its InternalIP: the API server cannot proxy requests (pod logs, exec) to this kubelet. " +
                "set VKUBELET_POD_IP to the IP the cluster uses to reach this machine",
                nodeName);
        }
        return localIp;
    }

    // The initial condition set is identical on node create and on every status update, so
    // it is built in one place: the two call sites cannot drift.
    private static List<V1NodeCondition> CreateNodeConditions(DateTime now)
    {
        List<V1NodeCondition> conditions =
        [
            new() { Status = ConditionTrue, Type = ConditionReady, LastHeartbeatTime = now, LastTransitionTime = now }
        ];
        conditions.AddRange(HealthyConditionTypes.Select(type => new V1NodeCondition
        {
            Status = ConditionFalse,
            Type = type,
            LastHeartbeatTime = now,
            LastTransitionTime = now
        }));
        return conditions;
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
