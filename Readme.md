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

## Getting Started

Follow these instructions to get started with using C# Virtual Kubelet:

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


