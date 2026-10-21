# Provider Guide

Implement a Sharplet provider by registering `IPodController` and `INodeController`
implementations via the `AddVirtualKubelet` DI callback. This page walks through both
interfaces, their required methods, and how to wire everything together.

## Overview

Sharplet is a virtual kubelet: it exposes the standard kubelet API endpoints but delegates
all pod and node lifecycle management to **your** provider code. There is no built-in
default provider — if you do not register both `IPodController` and `INodeController`,
the application throws at startup with an actionable error.

```csharp
throw new InvalidOperationException(
    "the virtual kubelet has no IPodController, INodeController registered. " +
    "Implement the interface(s) and register them via the configureProvider callback");
```

## Interfaces

### `IPodController`

Manages pod lifecycle on the virtual node.

| Method | Purpose |
|--------|---------|
| `CreatePodAsync(V1Pod, CancellationToken)` | Called when Kubernetes creates a pod scheduled on your node. The pod object already exists in the API server; only set up your local resources (containers, processes, etc.). Do **not** delete the API object — that would cause a 404 and crash the kubelet. |
| `UpdatePodAsync(V1Pod, CancellationToken)` | Called when an existing pod's spec changes. Update or recreate your backing resources as needed. |
| `DeletePodAsync(V1Pod, CancellationToken)` | Called when a pod is deleted from the API server. Release any local state (containers, files, ports). The pod has **already been removed** from the API. |
| `GetPodAsync(namespace, name, token)` | Return the full V1Pod for status reporting, or `null` if unknown. Used by the `/runningpods` endpoint. |
| `GetPodStatusAsync(namespace, name, token)` | Return a `V1PodStatus` describing container states, phase, conditions, IPs, etc. This is what `kubectl get pods` sees. |
| `GetPodsAsync(token)` | List all pods currently managed by this provider. |
| `GetContainerLogs(namespace, podname, containername, token)` | Yield log lines one at a time as an `IAsyncEnumerable<string>`. Terminate the stream when the cancellation token fires (client disconnect). |

### `INodeController`

Manages the virtual node itself.

| Method | Purpose |
|--------|---------|
| `CreateNodeAsync(V1Node, CancellationToken)` | Called on startup with the node definition. Create/update the node object in the API server with addresses, capacity, taints, labels, annotations, and conditions. Handle `Conflict` on re-creation gracefully — the node outlives the process. |
| `GetNodeAsync(nodeName, token)` | Return the current V1Node object. |
| `DeleteNodeAsync(nodeName, token)` | Delete the node object from the API. |
| `GetNodeStatusAsync(nodeName, token)` | Return the current V1NodeStatus. Called repeatedly by the PodControllerService and NodeControllerService. |

## Minimal Implementation

Here is a minimal provider that satisfies the contract. Replace the stub logic with your
actual integration code.

### Pod Controller

```csharp
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Sharplet.Core;

public class MyPodController : IPodController
{
    private readonly ILogger<MyPodController> _logger;
    private readonly IKubernetes _kubernetes;

    public MyPodController(ILogger<MyPodController> logger, IKubernetes kubernetes)
    {
        _logger = logger;
        _kubernetes = kubernetes;
    }

    public Task CreatePodAsync(V1Pod pod, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating pod {PodName} in namespace {Namespace}",
            pod.Name(), pod.Namespace());
        // TODO: Start containers, allocate resources, etc.
        return Task.CompletedTask;
    }

    public Task UpdatePodAsync(V1Pod pod, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Updating pod {PodName}", pod.Name());
        // TODO: Reconcile existing resources with new spec.
        return Task.CompletedTask;
    }

    public Task DeletePodAsync(V1Pod pod, CancellationToken cancellationToken = default)
    {
        // The pod was already deleted from the API by Kubernetes. Only release your
        // local state (containers, files, network resources, etc.).
        _logger.LogInformation("Deleting pod {PodName}", pod.Name());
        // TODO: Stop and clean up containers.
        return Task.CompletedTask;
    }

    public async Task<V1Pod?> GetPodAsync(string @namespace, string name,
        CancellationToken cancellationToken = default)
    {
        var pods = await _kubernetes.CoreV1.ListNamespacedPodAsync(@namespace,
            cancellationToken: cancellationToken);
        return pods.Items.FirstOrDefault(p => p.Name() == name);
    }

    public async Task<V1PodStatus> GetPodStatusAsync(string @namespace, string name,
        CancellationToken cancellationToken = default)
    {
        // Read the real pod from the API server to build accurate status.
        var pod = await _kubernetes.CoreV1.ReadNamespacedPodWithHttpMessagesAsync(
            name, @namespace, cancellationToken: cancellationToken);

        // Build container statuses based on your actual container state.
        var containerStatuses = pod.Body.Spec.Containers.Select(container =>
            new V1ContainerStatus
            {
                Image = container.Image,
                Name = container.Name,
                Ready = true,
                RestartCount = 0,
                Started = true,
                State = new V1ContainerState
                    { Running = new V1ContainerStateRunning { StartedAt = DateTime.UtcNow } }
            }).ToList();

        string localIp = Environment.GetEnvironmentVariable("VKUBELET_POD_IP")
            ?? Environment.GetEnvironmentVariable("POD_IP")
            ?? "127.0.0.1";

        return new V1PodStatus
        {
            Phase = "Running",
            ContainerStatuses = containerStatuses,
            HostIP = localIp,
            HostIPs = [new V1HostIP { Ip = localIp }],
            PodIP = localIp,
            PodIPs = [new V1PodIP { Ip = localIp }],
            Conditions =
            [
                new V1PodCondition { Status = "True", Type = "Initialized" },
                new V1PodCondition { Status = "True", Type = "Ready" },
                new V1PodCondition { Status = "True", Type = "ContainersReady" },
                new V1PodCondition { Status = "True", Type = "PodScheduled" }
            ]
        };
    }

    public async Task<IEnumerable<V1Pod>> GetPodsAsync(CancellationToken cancellationToken = default)
    {
        var list = await _kubernetes.CoreV1.ListPodForAllNamespacesWithHttpMessagesAsync(
            cancellationToken: cancellationToken);
        return list.Items;
    }

    public Task<IAsyncEnumerable<string>> GetContainerLogs(string @namespace, string podname,
        string containername, CancellationToken cancellationToken)
    {
        // TODO: Stream container logs. Each yielded string is one line without trailing newline.
        // The kubelet terminates streaming when this cancellation token fires.
        return null!;
    }
}
```

### Node Controller

```csharp
using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Sharplet.Core;

public class MyNodeController : INodeController
{
    private const string ConditionTrue = "True";
    private const string ConditionFalse = "False";
    private const string ConditionReady = "Ready";

    private readonly ILogger<MyNodeController> _logger;
    private readonly IKubernetes _kubernetes;

    public MyNodeController(ILogger<MyNodeController> logger, IKubernetes kubernetes)
    {
        _logger = logger;
        _kubernetes = kubernetes;
    }

    public async Task CreateNodeAsync(V1Node node, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Registering virtual node {NodeName}", node.Name());

        try
        {
            string localIp = await ResolveLocalIp(node.Name(), cancellationToken);

            await _kubernetes.CoreV1.CreateNodeAsync(new V1Node
            {
                Metadata = new V1ObjectMeta
                {
                    Name = node.Name(),
                    Annotations =
                    {
                        { "node.alpha.kubernetes.io/ttl", "0" },
                        { "volumes.kubernetes.io/controller-managed-attach-detach", "true" }
                    },
                    Labels =
                    {
                        { "kubernetes.io/role", "agent" },
                        { "type", "virtual-kubelet" },
                        { "kubernetes.io/hostname", node.Name() }
                    }
                },
                Spec = new V1NodeSpec
                {
                    PodCIDR = "10.244.0.0/24",
                    Taints =
                    [
                        new V1Taint { Effect = "NoSchedule", Key = "kubernetes.io/virtual-kubelet" },
                        new V1Taint { Effect = "NoExecute", Key = "kubernetes.io/virtual-kubelet" }
                    ]
                },
                Status = new V1NodeStatus
                {
                    Addresses =
                    [
                        new V1NodeAddress { Address = localIp, Type = "InternalIP" },
                        new V1NodeAddress { Address = node.Name(), Type = "Hostname" }
                    ],
                    Allocatable = new Dictionary<string, ResourceQuantity>
                    {
                        { "cpu", new ResourceQuantity("10") },
                        { "memory", new ResourceQuantity("4Gi") },
                        { "pods", new ResourceQuantity("5") }
                    },
                    Capacity = new Dictionary<string, ResourceQuantity>
                    {
                        { "cpu", new ResourceQuantity("10") },
                        { "memory", new ResourceQuantity("4Gi") },
                        { "pods", new ResourceQuantity("5") }
                    },
                    Conditions = [new V1NodeCondition
                    {
                        Status = ConditionTrue,
                        Type = ConditionReady
                    }],
                    DaemonEndpoints = new V1NodeDaemonEndpoints
                    { KubeletEndpoint = new V1DaemonEndpoint { Port = 10250 } }
                }
            }, cancellationToken: cancellationToken);
        }
        catch (HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
        {
            // The node object outlives the process. A conflict on re-creation means it
            // already exists — this is the normal path after every restart.
            _logger.LogInformation(ex, "node {NodeName} already exists; keeping existing", node.Name());
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
        // Mirror the status returned during CreateNodeAsync.
        return Task.FromResult(new V1NodeStatus
        {
            Addresses =
            [
                new V1NodeAddress { Address = "127.0.0.1", Type = "InternalIP" },
                new V1NodeAddress { Address = nodeName, Type = "Hostname" }
            ],
            Allocatable = new Dictionary<string, ResourceQuantity>
            {
                { "cpu", new ResourceQuantity("10") },
                { "memory", new ResourceQuantity("4Gi") },
                { "pods", new ResourceQuantity("5") }
            },
            Capacity = new Dictionary<string, ResourceQuantity>
            {
                { "cpu", new ResourceQuantity("10") },
                { "memory", new ResourceQuantity("4Gi") },
                { "pods", new ResourceQuantity("5") }
            },
            Conditions = [new V1NodeCondition
            {
                Status = ConditionTrue,
                Type = ConditionReady
            }]
        });
    }

    private async Task<string> ResolveLocalIp(string nodeName, CancellationToken ct)
    {
        // Prefer VKUBELET_POD_IP env var (set by Kubernetes Downward API), fall back
        // to localhost for local development. Empty values count as unset.
        var ip = Environment.GetEnvironmentVariable("VKUBELET_POD_IP")
            ?? Environment.GetEnvironmentVariable("POD_IP");

        if (!string.IsNullOrWhiteSpace(ip))
            return ip;

        // Try reading /etc/hosts or hostname resolution as last resort.
        return await Task.FromResult(Dns.GetHostName());
    }
}
```

## Wiring It Up

Register your controllers in `Program.cs` using the `AddVirtualKubelet` extension method.
The `configureProvider` callback receives an `IServiceCollection` — use `AddSingleton`
to register both interfaces along with any supporting services (HTTP clients, config
sources, etc.) that your provider depends on.

```csharp
using Sharplet.Core;
using Sharplet.Provider.MyRealImpl;  // your package / project

WebApplication.CreateBuilder(args)
    .Logging.WithJsonConsole()
    .Configuration.SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json")
        .AddJsonFile($"appsettings.{builder.Environment}.json", optional: true)
        .AddEnvironmentVariables()
    .AddVirtualKubelet(new SharpConfig
    {
        NodeName = "my-virtual-node",         // name used in the cluster
        PodStatusUpdateInterval = 15,          // seconds between pod status reports
        NodeStatusUpdateInterval = 30,         // seconds between node status reports
        NodeMaxPodCount = 6                    // maximum pods before rejection
    }, services =>
    {
        // Register your provider implementations.
        services.AddSingleton<IPodController, MyPodController>();
        services.AddSingleton<INodeController, MyNodeController>();

        // Add any supporting services here:
        // services.AddSingleton<IMyApi>(sp => new MyApiClient(config));
        // services.AddScoped<IDbConnection>(sp => ...);
    });

// Map kubelet API + health probe endpoints onto the application.
var app = builder.Build();
app.MapKubeletEndpoints();
await app.RunAsync();
```

### SharpConfig Options

| Property | Default | Description |
|----------|---------|-------------|
| `NodeName` | `"sharplet"` | Name of the virtual node as seen by the cluster |
| `PodStatusUpdateInterval` | `10` | Seconds between pod status polling |
| `NodeStatusUpdateInterval` | `30` | Seconds between node status polling |
| `NodeMaxPodCount` | `10` | Maximum pods before the kubelet rejects scheduling |

## Architecture Notes

### Service Lifetimes

Both `IPodController` and `INodeController` are registered as `Singleton` because the
built-in services (`PodControllerService`, `NodeControllerService`, `LeaderElectionService`)
resolve them once at construction time. Transient or scoped registrations will break.

### Kubernetes Client Injection

Sharplet automatically registers `IKubernetes` as a singleton. If your provider needs
direct Kubernetes API access (e.g. `CoreV1.ReadNamespacedPodAsync`), inject `IKubernetes`
through the constructor — do not call `BuildDefaultConfig()` yourself.

### Logging

Use `ILogger<T>` injected into your controllers. The kubelet writes structured JSON to
stdout by default. In production, ship these logs to your preferred observability backend.

### Certificates

The 10250 listener uses mutual TLS. When running inside a cluster, Sharplet auto-detects
the mounted CA bundle (`SHARPLET_CLIENT_CA` env var or the standard service account mount).
Without a CA bundle, certificates are still required but chain verification is skipped —
this should be configured for production deployments.

## Reference Implementations

The mock provider shipped with this repository demonstrates the pattern end-to-end:

- **MockPodController**: [`src/Sharplet.Provider.Mock/MockPodController.cs`](https://github.com/jimfim/Sharplet/tree/main/src/Sharplet.Provider.Mock/MockPodController.cs)
- **MockNodeController**: [`src/Sharplet.Provider.Mock/MockNodeController.cs`](https://github.com/jimfim/Sharplet/tree/main/src/Sharplet.Provider.Mock/MockNodeController.cs)
- **DI registration**: [`src/Sharplet.Samplekubelet/Program.cs`](https://github.com/jimfim/Sharplet/tree/main/src/Sharplet.Samplekubelet/Program.cs)

Study these files for concrete examples of handling edge cases such as:
- Node creation conflicts on restart (graceful Conflict → no-op)
- Pod condition constants ("True"/"False" rather than bools — the API silently accepts typos)
- Container log streaming with cancellation
- Using `VKUBELET_POD_IP` for correct IP reporting from within a pod

## Troubleshooting

### "no IPodController registered"

You forgot to register the controller in the `configureProvider` callback, or the type
doesn't implement the interface correctly. Verify the registration compiles:

```bash
dotnet build src -c Release
```

### Pod status shows CrashLoopBackOff

Ensure `GetPodStatusAsync` returns accurate container states. A common mistake is
reporting `Ready = false` when the backing container is actually running.

### Node never appears in `kubectl get nodes`

Check `CreateNodeAsync` doesn't throw. The node must be created with the TTL annotation
(`node.alpha.kubernetes.io/ttl: "0"`) so it survives process restarts. Also verify
your `InternalIP` address is reachable from the Kubernetes control plane.

### Container logs empty

`GetContainerLogs` must yield lines synchronously until the cancellation token fires.
If you buffer results and never yield, the kubelet times out waiting for data.
