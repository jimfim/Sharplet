# Sharplet - C# Virtual Kubelet

[![License](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)

Sharplet is an implementation of a Kubernetes node agent (similar to Virtual Kubelet) using C#. It allows Kubernetes clusters to seamlessly integrate with external systems or services by abstracting node management and pod execution.

## Overview

The goal of this project is to provide a simple way for developers to extend a Kubernetes cluster in C#. The project is designed to be flexible and easy to use, allowing developers to quickly get started with integrating their own systems or services with Kubernetes.

## Features

- **Flexible Integration**: Integrate Kubernetes clusters with custom external systems or services using C#.
- **Pod Scheduling**: Schedule pods onto the virtual node managed by the virtual kubelet.
- **Container Lifecycle Management**: Manage the lifecycle of containers running on the virtual node, including creation, deletion, and updates.
- **Resource Management**: Monitor and manage resource allocation and utilization on the virtual node.

## Purpose of this Repository

You won't find a functional provider in this repository. This project is intended as a reference for others implementing 3rd party providers. We provide a mock provider in this repo as a reference: it just spoofs running pods on the virtual kubelet without any system to back them up.

The Sharplet library provides an interface for managing the lifecycle of pods on a virtual node, including creation, deletion, and updates. It also includes methods for monitoring and managing resource allocation and utilization on the virtual node.

The intention is that this repository provides a NuGet package that you can consume in your own project to implement a provider using the documented interfaces. You only worry about integrating with your intended provider; the Kubernetes management is left to us.

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

## Debugging locally with minikube

Everything below runs against a throwaway minikube cluster: the only thing actually scheduled on the minikube VM is the `sharplet` pod itself — pods on the virtual node are spoofed by the mock provider, so minikube's default resources (2 CPUs / 2 GiB) are plenty.

### Prerequisites

- [minikube](https://minikube.sigs.k8s.io/download/)
- A container runtime that minikube can drive: Docker, Podman, or a VM driver (`qemu`, `podman`, ...)
- [Helm 3](https://helm.sh/docs/intro/install/)
- `kubectl` (or use `minikube kubectl -- <cmd>` / `minikube helm -- <cmd>` aliases)
- .NET 10 SDK — only if you want to run the kubelet outside the cluster (see [Debugging in your IDE](#debugging-in-your-ide))

### 1. Start minikube

```bash
minikube start
```

### 2. Build the kubelet image

The Dockerfile builds `Sharplet.Samplekubelet` (which composes `Sharplet.Core` + the mock provider) and runs it as `dotnet Sharplet.Samplekubelet.dll`.

```bash
docker build -t localhost/sharplet .
# or, with Podman:
podman build -t localhost/sharplet .
```

### 3. Load the image into minikube

The chart pins `image.repository: localhost/sharplet` with `pullPolicy: Never`, so the image must exist **inside** the cluster before install:

```bash
minikube image load localhost/sharplet
```

If your minikube driver is `docker`, this is a no-op (the Docker daemon *is* minikube's image store) but harmless to run. With other drivers (e.g. `podman`, `qemu`), `minikube image load` can fail to see host-built images (it reports the image as not found and falls back to a Docker Hub pull) — in that case point the Docker client at minikube's own daemon and build directly into the node's image store:

```bash
eval "$(minikube docker-env)"    # sets DOCKER_HOST to minikube's daemon
docker build -t localhost/sharplet .
```

which replaces the separate load step. If the image is missing in-cluster the pod ends up in `ImagePullBackOff`.

### 4. Install the chart

```bash
helm install sharplet ./charts/sharplet
```

This creates:

| Resource | Notes |
|---|---|
| Deployment `sharplet` | container `sharplet`, image `localhost/sharplet:latest` |
| Service `sharplet` | ClusterIP on port `10250` |
| ServiceAccount + ClusterRole | `nodes` (create/get), `nodes/status` (patch/update), `pods`, `pods/status`, `events`, `leases` |
| Secret `sharplet` | apiserver cert/key, **auto-generated by Helm** (`genSignedCert`), mounted at `/etc/virtual-kubelet` |

The pod listens on two ports:

- `10250` — the kubelet API server (HTTPS, client certs accepted).
- `10255` — the same endpoints over plain HTTP; handy for local poking since it needs no client cert.

Cert locations are read from the `APISERVER_CERT_LOCATION` / `APISERVER_KEY_LOCATION` env vars (set by the chart to `/etc/virtual-kubelet/...`); if they're unset the code falls back to `/etc/sharplet/cert.pem` / `key.pem`. To supply your own certificates instead (e.g. from the `Sharplet.CSR` tool or an external CA), pass base64 `apiserverCert` / `apiserverKey` values to the release.

### 5. Watch the virtual node come up

The kubelet registers itself as node `sharplet` (label `type=virtual-kubelet`, taints `kubernetes.io/sharplet:NoSchedule` and `:NoExecute`, advertising 10 CPU / ~4 Gi / 5 pods):

```bash
kubectl get nodes -w            # sharplet appears within a few seconds
kubectl describe node sharplet  # address should be the pod IP, not 127.0.0.1
kubectl logs -f -l app.kubernetes.io/name=sharplet
```

The pod IP is injected via `VKUBELET_POD_IP` (the chart's `fieldRef: status.podIP`); it becomes the node's `InternalIP`, which is what the API server dials when talking to the kubelet.

### 6. Schedule a sample pod on the virtual node

```bash
kubectl apply -f test.yaml
kubectl get pods -w             # nginx -> NODE sharplet, quickly reports Running
kubectl describe pod <pod>      # check events
```

The pod tolerates `kubernetes.io/sharplet` so the scheduler places it on the virtual node. The mock provider spoofs the pod as running and the kubelet's status tracker patches its status — **no container is actually started**, so `kubectl exec` into it won't work and any workload inside it is imaginary. Two mock quirks to expect: the pod reports the kubelet's own pod IP as its `podIP`/`hostIP`, and it reports `restartCount: 1`, so `RESTARTS 1` in `kubectl get pods` is the mock, not a crash.

To talk to the kubelet API locally (e.g. the mock container-logs endpoint), port-forward the plain-HTTP listener:

```bash
kubectl port-forward pod/$(kubectl get pod -l app.kubernetes.io/name=sharplet -o jsonpath='{.items[0].metadata.name}') 10255:10255 &
curl localhost:10255/containerLogs/default/<virtual-pod-name>/<container-name>
```

### 7. The edit/rebuild/redeploy loop

After changing code under `src/`:

```bash
docker build -t localhost/sharplet .
minikube image load localhost/sharplet
kubectl rollout restart deployment/sharplet
kubectl logs -f -l app.kubernetes.io/name=sharplet
```

`rollout restart` recreates the pod, which re-pulls the (never-pulled) `localhost/sharplet:latest` image — i.e. the one you just loaded.

### Debugging in your IDE

Instead of the in-cluster loop you can run the sample kubelet on your workstation and hit breakpoints:

```bash
# minikube start writes the cluster's kubeconfig to ~/.kube/config automatically

# certs: either create /etc/sharplet/... or point the env vars at a local dir
export APISERVER_CERT_LOCATION="$PWD/certs/cert.pem"
export APISERVER_KEY_LOCATION="$PWD/certs/key.pem"

dotnet run --project src/Sharplet.Samplekubelet
```

Outside a pod there's no `VKUBELET_POD_IP`, so the node falls back to `127.0.0.1` as its address. That's fine for watching the kubelet-side watch/patch logic in a debugger, but the API server cannot proxy to the kubelet at that address: `127.0.0.1` resolves against the API server's own loopback (on minikube that's the VM's real kubelet, which doesn't know the virtual pods and answers `404`), so `kubectl logs` and k9s show nothing for pods on the virtual node. The kubelet logs a warning when it advertises the loopback address.

To get `kubectl logs` and k9s streaming pod logs while debugging on your workstation, set `VKUBELET_POD_IP` to the IP the cluster's API server uses to reach your machine, then start the kubelet:

- **minikube with a VM driver (podman, qemu, ...)** — the host IP as seen from the minikube VM, i.e. the gateway of the VM's default route:
  `minikube ssh "ip route show default"` → the address on the `default via <ip>` line.
- **minikube with the docker driver** — the Docker bridge gateway on the host, usually `172.17.0.1`.

```bash
export VKUBELET_POD_IP=192.168.49.1   # e.g. the minikube podman/qemu host as seen from the VM
export APISERVER_CERT_LOCATION="$HOME/.sharplet/cert.pem"
export APISERVER_KEY_LOCATION="$HOME/.sharplet/key.pem"

dotnet run --project src/Sharplet.Samplekubelet
```

Within one node-status tick (≤ 30 s) the node re-advertises that address and the API server's proxy reaches the kubelet on `10250` — no port-forward needed. Clusters that verify the kubelet's serving certificate against the dialed IP will also want a cert whose SAN contains that IP: set `VKUBELET_POD_IP` and re-run `dotnet run --project src/Sharplet.CSR` before starting the kubelet (it adds the pod IP and the node name to the SAN, signed by the cluster CA).

### Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| `sharplet` pod in `ImagePullBackOff` | Image not loaded into minikube: `minikube image load localhost/sharplet`, then `kubectl rollout restart deployment/sharplet`. |
| `kubectl exec` into the `sharplet` pod fails (no shell) | The runtime image is Ubuntu Chiseled (distroless: no shell, no package manager, no OS CA store). Use `kubectl port-forward` on the `10255` port (see step 6) or an ephemeral debug container (`kubectl debug`) instead of `exec`. |
| `sharplet` pod `CrashLoopBackOff` with `FileNotFoundException: /etc/sharplet/cert.pem` | Stale image that predates `APISERVER_CERT_LOCATION` support — rebuild the image, or mount the secret where the old code looks: `helm upgrade sharplet ./charts/sharplet --set 'volumeMounts[0].mountPath=/etc/sharplet'`. |
| Node `sharplet` never appears, or stays `NotReady` | `kubectl logs -l app.kubernetes.io/name=sharplet` — usually RBAC or a missing in-cluster config. `kubectl get events` helps too. |
| Sample pod stuck `Pending` | It must carry the `kubernetes.io/sharplet` toleration (it's in `test.yaml`); confirm the `sharplet` node is `Ready` and the kubelet status tracker is running (logs). |
| `kubectl logs <virtual pod>` fails with an x509 error | The API server validates the kubelet's serving certificate. The chart-generated cert is signed by the chart's own CA, which most clusters don't trust — issue a cluster-CA-signed cert with the `Sharplet.CSR` tool instead (it also puts the node name and `VKUBELET_POD_IP`, when set, into the SAN). On minikube the chart-generated cert happens to be accepted. |
| `kubectl logs` / k9s show nothing for a virtual pod (API server answers `404` for `pods/log`) | The node advertises `127.0.0.1` as its `InternalIP`, so the API server proxies the log request to its own loopback instead of the virtual kubelet (on minikube: the VM's real kubelet, which doesn't know the pod). In-cluster: the pod isn't getting `VKUBELET_POD_IP` — the chart sets it; a hand-rolled manifest needs `valueFrom: fieldRef: status.podIP` under that name. Running on your workstation: see [Debugging in your IDE](#debugging-in-your-ide) — set `VKUBELET_POD_IP` to the IP the cluster uses to reach your machine. |

### Teardown

```bash
helm uninstall sharplet
kubectl delete -f test.yaml
minikube stop        # or: minikube delete
```