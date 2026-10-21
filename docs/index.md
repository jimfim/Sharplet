# Writing a Provider

Sharplet is a virtual kubelet — it runs as a Kubernetes node agent and registers itself as a node that can accept scheduled pods. Your job is **one thing**: when Kubernetes asks Sharplet to create, update, or delete a pod, you decide what actually happens.

This guide shows how to implement `IPodController` and `INodeController`, register them via DI, and run your provider in-cluster or locally.

---

## The Two Interfaces

Every Sharplet provider implements two interfaces from `Sharplet.Core`:

### IPodController

Manages the lifecycle of pods scheduled onto your virtual node:

```csharp
public interface IPodController
{
    Task CreatePodAsync(V1Pod pod, CancellationToken cancellationToken = default);
    Task UpdatePodAsync(V1Pod pod, CancellationToken cancellationToken = default);
    Task DeletePodAsync(V1Pod pod, CancellationToken cancellationToken = default);
    Task<V1Pod?> GetPodAsync(string @namespace, string name, CancellationToken cancellationToken = default);
    Task<V1PodStatus> GetPodStatusAsync(string @namespace, string name, CancellationToken cancellationToken = default);
    Task<IEnumerable<V1Pod>> GetPodsAsync(CancellationToken cancellationToken = default);
    Task<IAsyncEnumerable<string>> GetContainerLogs(string @namespace, string podname, string containername, CancellationToken cancellationToken);
}
```

| Method | When called | What to do |
|---|---|---|
| `CreatePodAsync` | Scheduler places a pod on your node | Start whatever represents that workload (submit to an API, spawn a process, queue a message) |
| `UpdatePodAsync` | Pod spec changes (e.g. image tag roll) | Apply the diff to your backing system |
| `DeletePodAsync` | Pod is deleted / rescheduled away | Release resources; **do not** delete the K8s API object — the apiserver already did that |
| `GetPodAsync` | Status tracker wants live pod details | Look up by namespace + name in your store, or proxy to the API server |
| `GetPodStatusAsync` | Every status tick (`PodStatusUpdateInterval`) | Return a populated `V1PodStatus`. This is the most important method — if your containers never report `Ready: true`, pods stay in Pending. See the Mock provider for a reference implementation. |
| `GetPodsAsync` | Initial sync / listing | Return all pods assigned to this node |
| `GetContainerLogs` | `kubectl logs <pod> -c <container>` | Stream log lines (no trailing newline). Yielding stops automatically when the token fires (client disconnect). |

#### V1PodStatus basics

Kubernetes determines whether a pod is running solely from the status you return. At minimum you need:

- `Phase = "Running"` (or `"Succeeded"` / `"Failed"`)
- `ContainerStatuses` with each container marked `Ready = true`
- Pod-level conditions (`Initialized`, `Ready`, `ContainersReady`, `PodScheduled`) all set to `Status = "True"`
- `PodIP` and `HostIP` (the kubelet's own address; `VKUBELET_POD_IP` env var is best)

The mock provider at [`src/Sharplet.Provider.Mock/MockPodController.cs`](https://github.com/jimfim/Sharplet/blob/develop/src/Sharplet.Provider.Mock/MockPodController.cs) gives a complete example. It always reports Running regardless of external state, which makes it a good starting template.

### INodeController

Manages the virtual node itself — created when the kubelet starts, updated periodically:

```csharp
public interface INodeController
{
    Task CreateNodeAsync(V1Node node, CancellationToken cancellationToken = default);
    Task<V1Node> GetNodeAsync(string nodeName, CancellationToken cancellationToken = default);
    Task DeleteNodeAsync(string nodeName, CancellationToken cancellationToken = default);
    Task<V1NodeStatus> GetNodeStatusAsync(string nodeName, CancellationToken cancellationToken = default);
}
```

| Method | When called | What to do |
|---|---|---|
| `CreateNodeAsync` | Kubelet starts | Register yourself as a node with taints, labels, capacity, addresses |
| `GetNodeAsync` | Periodic readback | Return the current node object |
| `DeleteNodeAsync` | Kubelet shutting down | Clean up your node registration |
| `GetNodeStatusAsync` | Every status tick (`NodeStatusUpdateInterval`) | Return updated status including conditions, addresses, allocatable resources |

Your node must advertise:

- Taints so only pods with matching tolerations land here: `kubernetes.io/sharplet:NoSchedule` and `kubernetes.io/sharplet:NoExecute`
- Labels including `type: virtual-kubelet` and `kubernetes.io/hostname: <your-node-name>`
- A valid `InternalIP` address — this is what the API server dials for proxying requests (pod logs, exec). Set `VKUBELET_POD_IP` to the cluster-reachable address.
- Realistic resource capacity and allocatable values

See [`src/Sharplet.Provider.Mock/MockNodeController.cs`](https://github.com/jimfim/Sharplet/blob/develop/src/Sharplet.Provider.Mock/MockNodeController.cs) for a complete implementation including conflict handling on node create and proper condition initialization.

---

## Setting Up DI

Register your controllers in the startup program. The entry point is `AddVirtualKubelet()`, which wires up the kubelet listener, kubernetes client, leader election, and status loops. Your callback registers the concrete providers:

```csharp
using Sharplet.Core;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.AddConsole();
builder.Configuration.SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json")
    .AddJsonFile($"appsettings.{builder.Environment}.Name".ToLowerInvariant() + ".json", optional: true, true);
builder.Configuration.AddEnvironmentVariables();

builder.AddVirtualKubelet(new SharpConfig
{
    NodeName = "my-provider",          // the name registered as a K8s node
    PodStatusUpdateInterval = 15,      // seconds between pod status patches
    NodeStatusUpdateInterval = 30,     // seconds between node status patches
    NodeMaxPodCount = 10               // maximum pods reported on the node
}, services =>
{
    services.AddSingleton<IPodController, MyPodController>();
    services.AddSingleton<INodeController, MyNodeController>();
});

WebApplication app = builder.Build();
app.MapKubeletEndpoints();
app.Run();
```

If either `IPodController` or `INodeController` is missing from DI, `AddVirtualKubelet()` throws an `InvalidOperationException` with instructions about what interface is missing.

Key points:

- Use `AddSingleton` — one controller instance per application lifetime
- Both are resolved through the shared `IKubernetes` client injected into their constructors (provided by the kubelet service wiring)
- Configure logging (`builder.Logging.AddConsole()` or `AddJsonConsole()`) so you can trace provider activity

---

## Configuration Reference

`SharpConfig` controls kubelet behavior:

| Property | Default | Description |
|---|---|---|
| `NodeName` | `"sharplet"` | The K8s node name to register |
| `PodStatusUpdateInterval` | `10` | Seconds between pod status patches to the API server |
| `NodeStatusUpdateInterval` | `30` | Seconds between node status patches |
| `NodeMaxPodCount` | `10` | Max pods advertised in the node's capacity |

Environment variables your controllers should respect:

| Variable | Description |
|---|---|
| `VKUBELET_POD_IP` | Pod IP of the kubelet itself — use this as `InternalIP` so the API server can reach you |
| `POD_IP` | Fallback if `VKUBELET_POD_IP` is unset |
| `APISERVER_CERT_LOCATION` | Path to the kubelet TLS certificate for the 10250 listener |
| `APISERVER_KEY_LOCATION` | Path to the corresponding private key |

---

## Running Locally

Run against a minikube cluster from your workstation:

```bash
export APISERVER_CERT_LOCATION="$PWD/certs/cert.pem"
export APISERVER_KEY_LOCATION="$PWD/certs/key.pem"
dotnet run --project src/MyProvider
```

Outside a pod there is no `VKUBELET_POD_IP`, so the node falls back to `127.0.0.1`. This works for debugging but the API server cannot proxy to it. To make `kubectl logs` work locally, set `VKUBELET_POD_IP` to the IP the cluster uses to reach your machine:

- Minikube with VM driver (podman, qemu): `minikube ssh "ip route show default"` → gateway address
- Minikube with docker driver: typically `172.17.0.1`

---

## Running In-Cluster

Package your provider as a Docker image (use `Sharplet.Samplekubelet/Dockerfile` as a template) and deploy via Helm:

```bash
helm install my-provider ./charts/my-provider \
  --set image.repository=myregistry/my-provider \
  --set image.tag=v1.0.0
```

Ensure your chart mounts certificates and sets the correct environment variables (same structure as the built-in chart at [`charts/sharplet`](https://github.com/jimfim/Sharplet/tree/develop/charts/sharplet)).

---

## Reference Implementation

The mock provider at [`src/Sharplet.Provider.Mock/`](https://github.com/jimfim/Sharplet/tree/develop/src/Sharplet.Provider.Mock) is the canonical reference:

- [`MockPodController.cs`](https://github.com/jimfim/Sharplet/blob/develop/src/Sharplet.Provider.Mock/MockPodController.cs) — all `IPodController` methods; returns Running status for everything
- [`MockNodeController.cs`](https://github.com/jimfim/Sharplet/blob/develop/src/Sharplet.Provider.Mock/MockNodeController.cs) — all `INodeController` methods; handles conflicts, builds proper node conditions
- [`Sharplet.Samplekubelet/Program.cs`](https://github.com/jimfim/Sharplet/blob/develop/src/Sharplet.Samplekubelet/Program.cs) — DI wiring showing exactly how `AddVirtualKubelet()` accepts provider registrations

When building a real provider, start by copying the mock implementations, then replace the stub methods with actual logic for your integration layer. The mock's status-reporting code is production-ready and needs no changes for a real-world deployment.
