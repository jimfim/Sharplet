using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Sharplet.Core;

namespace Sharplet.Provider.Mock;

/// <summary>
/// This just updates the apiserver to say everything is running smoothly. even though there is nothing backing it.
/// </summary>
public class MockPodController : IPodController
{
    private readonly ILogger<MockPodController> _logger;
    private readonly IKubernetes _kubernetes;

    public MockPodController(ILogger<MockPodController> logger, IKubernetes kubernetes)
    {
        _logger = logger;
        _kubernetes = kubernetes;
    }

    public Task CreatePodAsync(V1Pod pod, CancellationToken cancellationToken = default)
    {
        //Pods.Add(pod);
        return Task.CompletedTask;
    }

    public async Task UpdatePodAsync(V1Pod pod, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("UpdatePodAsync {PodName}", pod.Name());
        var podAsync = await _kubernetes.CoreV1.ListNamespacedPodAsync(pod.Namespace(), cancellationToken: cancellationToken);
        
    }

    public Task DeletePodAsync(V1Pod pod, CancellationToken cancellationToken = default)
    {
        // the pod object was already deleted by the user/controller; a provider only releases its local
        // state. Deleting the API object here would 404 and crash the kubelet.
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("DeletePodAsync {PodName}", pod.Name());
        }
        return Task.CompletedTask;
    }

    public async Task<V1Pod?> GetPodAsync(string @namespace, string name, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GetPodAsync");
        var podAsync = await _kubernetes.CoreV1.ListNamespacedPodAsync(@namespace, cancellationToken: cancellationToken);
        return podAsync.Items.FirstOrDefault(x => x.Name() == name);
    }

    public async Task<V1PodStatus> GetPodStatusAsync(string @namespace, string name,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GetPodStatusAsync");
        var podAsync = await _kubernetes.CoreV1.ReadNamespacedPodWithHttpMessagesAsync(name, @namespace, cancellationToken: cancellationToken);

        var containerStatusList = podAsync.Body.Spec.Containers.Select(container => new V1ContainerStatus
            {
                Image = container.Image,
                Name = container.Name,
                Ready = true,
                RestartCount = 0,
                Started = true,
                State = new V1ContainerState { Running = new V1ContainerStateRunning { StartedAt = DateTime.Now } }
            })
            .ToList();
        string localIp = Environment.GetEnvironmentVariable("VKUBELET_POD_IP") ?? Environment.GetEnvironmentVariable("POD_IP") ?? "127.0.0.1";
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("setting pod ip to {PodIp}", localIp);
        }
        var status = new V1PodStatus
        {
            Phase = "Running",
            ContainerStatuses = containerStatusList,
            HostIP = localIp,
            HostIPs = new List<V1HostIP>
            {
                new V1HostIP { Ip = localIp }
            },
            PodIP = localIp,
            PodIPs = new List<V1PodIP>
            {
                new V1PodIP { Ip = localIp }
            },
            Conditions = new List<V1PodCondition>
            {
                new()
                {
                    Status = "True",
                    Type = "Initialized",
                    LastProbeTime = DateTime.Now,
                    LastTransitionTime = DateTime.Now
                },
                new()
                {
                    Status = "True",
                    Type = "Ready",
                    LastProbeTime = DateTime.Now,
                    LastTransitionTime = DateTime.Now
                },
                new()
                {
                    Status = "True",
                    Type = "ContainersReady",
                    LastProbeTime = DateTime.Now,
                    LastTransitionTime = DateTime.Now
                },
                new()
                {
                    Status = "True",
                    Type = "PodScheduled",
                    LastProbeTime = DateTime.Now,
                    LastTransitionTime = DateTime.Now
                }
            }
        };
        return status;
    }

    public async Task<IEnumerable<V1Pod>> GetPodsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("GetPodsAsync");
        var pods = await _kubernetes.CoreV1.ListNamespacedPodAsync("default", cancellationToken: cancellationToken);
        return pods.Items;
    }
    
    public Task<IAsyncEnumerable<string>> GetContainerLogs(string @namespace, string podname, string containername, CancellationToken cancellationToken)
    {
        var response = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            response.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {@namespace} {podname} {containername} Log message from Provider {i}");
        }

        return Task.FromResult(GetLogs());
        async IAsyncEnumerable<string> GetLogs()
        {
            foreach (var entry in response)
            {
                await Task.Delay(1000, cancellationToken);
                yield return entry;
            }
        }
    }
}
