# Sharplet - C# Virtual Kubelet

[![License](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)

C# Virtual Kubelet is an implementation of a Kubernetes node agent (similar to Virtual Kubelet) using C#. It allows Kubernetes clusters to seamlessly integrate with external systems or services by abstracting node management and pod execution.

## Overview
The goal of this project is to provide a simple way for developers to use C# to extend a Kubernetes clusters. The project is designed to be flexible and easy to use, allowing developers to quickly get started with integrating their own systems or services with Kubernetes.

## Features

- **Flexible Integration**: Integrate Kubernetes clusters with custom external systems or services using C#.
- **Pod Scheduling**: Schedule pods onto the virtual node managed by the Sharplet.
- **Container Lifecycle Management**: Manage the lifecycle of containers running on the virtual node, including creation, deletion, and updates.
- **Resource Management**: Monitor and manage resource allocation and utilization on the virtual node.

## Purpose of thie Repository
You won't find a functional providein this repository. this project is intended as a reference for other implementing 3rd party providers. the 
Mock Provider provided just mocks running pods on the virutla kubelet without any system to back them up

The Sharplet library provides an interface for managing the lifecycle of pods on a virtual node, including creation, deletion, and updates. It also includes methods for monitoring and managing resource allocation and utilization on the virtual node.

The intention is that this repository will provide a nuget pacakge that you can consume in your own project to implement a Provider using the documented interfaces leaving you to worry about integrating with your intended provider, leaving the kubernetes management to us

```bash
public interface IPodLifeCycle
{
    Task CreatePodAsync(V1Pod pod, CancellationToken cancellationToken = default);
    Task UpdatePodAsync(V1Pod pod, CancellationToken cancellationToken = default);
    Task DeletePodAsync(V1Pod pod, CancellationToken cancellationToken = default);
    Task<V1Pod?> GetPodAsync(string @namespace, string name, CancellationToken cancellationToken = default);
    Task<V1PodStatus> GetPodStatusAsync(string @namespace, string name, CancellationToken cancellationToken = default);
    Task<IEnumerable<V1Pod>> GetPodsAsync(CancellationToken cancellationToken = default);
}
```

## Getting Started

Follow these instructions to get started with using Sharplet:

### Prerequisites

- .NET SDK (version X.X or higher)
- Kubernetes cluster (version X.X or higher)

### Build from source

```bash
docker build -t localhost/dotnet-kubelet .
```


### Installation

Clone the repository:

```bash
git clone https://github.com/yourusername/csharp-virtual-kubelet.git
```

Certificate Signing Request

```bash
coming soon
```

in the project root
```bash
docker build -t localhost/dotnet-kubelet:latest .
helm install vkcs ./charts/dotnet-kubelet
```


