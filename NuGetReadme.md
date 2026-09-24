# Sharplet.Core

Sharplet is a reference implementation of a Kubernetes node agent (virtual kubelet) in C#. `Sharplet.Core` is the provider-agnostic half: it watches the Kubernetes API server, runs the kubelet's HTTP surface (the client-cert `10250` API port and the read-only `10255` metrics port), and reports pod and node status from your provider. The package ships **no provider** — you implement the small contracts below and back the virtual node with your own infrastructure. `Sharplet.Provider.Mock` is a separate package: a reference implementation that spoofs running pods and demonstrates the contract.

## Getting started

```csharp
using Sharplet.Core;

builder.AddVirtualKubelet(new SharpConfig
{
    NodeName = "my-node",
    PodStatusUpdateInterval = 15,
    NodeStatusUpdateInterval = 30,
    NodeMaxPodCount = 6
}, services =>
{
    services.AddSingleton<IPodController, MyPodController>();
    services.AddSingleton<INodeController, MyNodeController>();
});

WebApplication app = builder.Build();
app.MapKubeletEndpoints();
app.Run();
```

`AddVirtualKubelet` registers the kubernetes client, leader election, and the hosted status services, and configures the Kestrel listeners; a kubelet that registers no `IPodController`/`INodeController` refuses to start. `MapKubeletEndpoints` exposes `/pods`, `/runningpods`, `/stats/sum`, `/containerLogs` and the health probes, which the API server proxies for `kubectl get pods` / `kubectl logs` against pods on the virtual node.

## Provider contracts

| Interface | Your job |
|---|---|
| `IPodController` | Pod lifecycle on your backend: `CreatePodAsync` / `UpdatePodAsync` / `DeletePodAsync`, status via `GetPodStatusAsync`, the pod list via `GetPodsAsync`, container logs via `GetContainerLogs`. |
| `INodeController` | Node lifecycle and status: `CreateNodeAsync` / `GetNodeAsync` / `DeleteNodeAsync` and `GetNodeStatusAsync`. |
| `IEventWatcher` | The pod watch that drives your `IPodController` when pods are scheduled onto the virtual node; `AddVirtualKubelet` registers a ready-made implementation. |

The core reports what you tell it: it patches each pod's `status` subresource on its `PodStatusUpdateInterval` tick and calls you when the API server creates, updates, or deletes a pod for the node.

The full guide — cluster setup, the reference mock provider, and the deployable `Sharplet.Samplekubelet` image with its Helm chart — lives in the repository: <https://github.com/jimfim/Sharplet>.