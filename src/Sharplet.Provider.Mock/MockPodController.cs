using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Sharplet.Core;

namespace Sharplet.Provider.Mock;

/// <summary>
/// Reference <see cref="IPodController"/> that reports every pod on the virtual node as running smoothly,
/// even though there is nothing backing it.
/// </summary>
/// <remarks>
/// The <c>sharplet.io/mock-behavior</c> pod annotation selects the status shape to report: <c>healthy</c>
/// (the default), <c>notready</c> (running but not ready), <c>liveness-fail</c> (a container terminated by
/// a failed liveness probe), and <c>crashloop</c> (a container in <c>CrashLoopBackOff</c> with a restart
/// count that climbs over time). A missing or unknown value is reported as <c>healthy</c> and logged at
/// debug level.
/// </remarks>
public class MockPodController : IPodController
{
    private readonly ILogger<MockPodController> _logger;
    private readonly IKubernetes _kubernetes;

    private const string MockBehaviorAnnotation = "sharplet.io/mock-behavior";

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
        HttpOperationResponse<V1Pod> response = await _kubernetes.CoreV1
            .ReadNamespacedPodWithHttpMessagesAsync(name, @namespace, cancellationToken: cancellationToken);
        V1Pod pod = response.Body;
        string behavior = GetMockBehavior(pod);
        if (IsUnknownBehavior(behavior))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("unknown {Annotation} value {Behavior}, reporting the healthy shape",
                    MockBehaviorAnnotation, behavior);
            }
        }

        return behavior switch
        {
            "notready" => BuildNotReadyStatus(pod),
            "liveness-fail" => BuildLivenessFailStatus(pod),
            "crashloop" => BuildCrashLoopStatus(pod),
            _ => BuildHealthyStatus(pod),
        };
    }

    /// <summary>
    /// Reads the <c>sharplet.io/mock-behavior</c> pod annotation; a missing or blank value means
    /// <c>healthy</c>.
    /// </summary>
    private static string GetMockBehavior(V1Pod pod)
    {
        string? behavior = pod.Metadata?.Annotations is { } annotations
            && annotations.TryGetValue(MockBehaviorAnnotation, out string? value)
                ? value
                : null;
        return string.IsNullOrWhiteSpace(behavior) ? "healthy" : behavior;
    }

    private static bool IsUnknownBehavior(string behavior)
        => behavior is not ("healthy" or "notready" or "liveness-fail" or "crashloop");

    private V1PodStatus BuildHealthyStatus(V1Pod pod)
    {
        DateTime now = DateTime.UtcNow;
        List<V1ContainerStatus> containerStatuses = pod.Spec.Containers.Select(container => new V1ContainerStatus
            {
                Image = container.Image,
                Name = container.Name,
                Ready = true,
                RestartCount = 0,
                Started = true,
                State = new V1ContainerState { Running = new V1ContainerStateRunning { StartedAt = now } },
            })
            .ToList();

        return BuildStatus(containerStatuses, ready: true);
    }

    private V1PodStatus BuildNotReadyStatus(V1Pod pod)
    {
        DateTime now = DateTime.UtcNow;
        List<V1ContainerStatus> containerStatuses = pod.Spec.Containers.Select(container => new V1ContainerStatus
            {
                Image = container.Image,
                Name = container.Name,
                Ready = false,
                RestartCount = 0,
                Started = true,
                State = new V1ContainerState { Running = new V1ContainerStateRunning { StartedAt = now } },
            })
            .ToList();

        // A pod that fails its readiness probe stays Running; only the ready gates flip to False.
        return BuildStatus(containerStatuses, ready: false);
    }

    private V1PodStatus BuildLivenessFailStatus(V1Pod pod)
    {
        DateTime now = DateTime.UtcNow;
        DateTime startedAt = GetPodStart(pod);
        List<V1ContainerStatus> containerStatuses = pod.Spec.Containers.Select(container => new V1ContainerStatus
            {
                Image = container.Image,
                Name = container.Name,
                Ready = false,
                RestartCount = 1,
                Started = false,
                State = new V1ContainerState
                {
                    // The run the kubelet just killed for failing its liveness probe.
                    Terminated = new V1ContainerStateTerminated
                    {
                        ExitCode = 2,
                        Reason = "Error",
                        Message = "Liveness probe failed",
                        StartedAt = startedAt,
                        FinishedAt = now,
                    },
                },
                // The run before it: alive from pod start until the kubelet killed it.
                LastState = new V1ContainerState
                {
                    Running = new V1ContainerStateRunning { StartedAt = startedAt },
                },
            })
            .ToList();

        return BuildStatus(containerStatuses, ready: false);
    }

    private V1PodStatus BuildCrashLoopStatus(V1Pod pod)
    {
        DateTime now = DateTime.UtcNow;
        DateTime startedAt = GetPodStart(pod);
        // Derived from the pod's start time, so the count climbs on every status tick without
        // any provider-side state.
        int restarts = CrashLoopBackoff.RestartCountAfter(now - startedAt);
        string backoff = CrashLoopBackoff.FormatDuration(CrashLoopBackoff.NextRestartDelay(restarts));
        string podFullName = $"{pod.Metadata?.NamespaceProperty ?? "default"}/{pod.Metadata?.Name}";
        List<V1ContainerStatus> containerStatuses = pod.Spec.Containers.Select(container => new V1ContainerStatus
            {
                Image = container.Image,
                Name = container.Name,
                Ready = false,
                RestartCount = restarts,
                Started = false,
                State = new V1ContainerState
                {
                    Waiting = new V1ContainerStateWaiting
                    {
                        Reason = "CrashLoopBackOff",
                        Message = $"back-off {backoff} restarting failed container={container.Name},pod={podFullName}/{container.Name}",
                    },
                },
                LastState = new V1ContainerState
                {
                    Terminated = new V1ContainerStateTerminated
                    {
                        ExitCode = 2,
                        Reason = "Error",
                        StartedAt = startedAt,
                        FinishedAt = now,
                    },
                },
            })
            .ToList();

        return BuildStatus(containerStatuses, ready: false);
    }

    /// <summary>
    /// The scaffolding every shape reports: the pod is Running, its addresses are the kubelet's own
    /// pod IP (so the API server can proxy to it), and the pod conditions reflect
    /// <paramref name="ready"/>.
    /// </summary>
    private V1PodStatus BuildStatus(List<V1ContainerStatus> containerStatuses, bool ready)
    {
        string localIp = Environment.GetEnvironmentVariable("VKUBELET_POD_IP") ?? Environment.GetEnvironmentVariable("POD_IP") ?? "127.0.0.1";
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("setting pod ip to {PodIp}", localIp);
        }

        DateTime now = DateTime.UtcNow;
        return new V1PodStatus
        {
            Phase = "Running",
            ContainerStatuses = containerStatuses,
            HostIP = localIp,
            HostIPs = new List<V1HostIP> { new V1HostIP { Ip = localIp } },
            PodIP = localIp,
            PodIPs = new List<V1PodIP> { new V1PodIP { Ip = localIp } },
            Conditions = new List<V1PodCondition>
            {
                new() { Type = "Initialized", Status = "True", LastProbeTime = now, LastTransitionTime = now },
                new() { Type = "Ready", Status = ready ? "True" : "False", LastProbeTime = now, LastTransitionTime = now },
                new() { Type = "ContainersReady", Status = ready ? "True" : "False", LastProbeTime = now, LastTransitionTime = now },
                new() { Type = "PodScheduled", Status = "True", LastProbeTime = now, LastTransitionTime = now },
            },
        };
    }

    /// <summary>
    /// When the container first started: the API server sets <c>status.startTime</c> once it has
    /// observed the pod Running, so a pod that never did falls back to its creation timestamp.
    /// </summary>
    private static DateTime GetPodStart(V1Pod pod)
        => pod.Status?.StartTime ?? pod.Metadata?.CreationTimestamp ?? DateTime.UtcNow;

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
