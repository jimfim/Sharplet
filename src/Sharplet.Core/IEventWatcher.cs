using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Sharplet.Abstractions;

namespace Sharplet.Core;

public interface IEventWatcher
{
    Task WatchEventStream();
}

internal class EventWatcher : IEventWatcher
{
    private readonly SharpConfig _config;
    private readonly IKubernetes _kubernetes;
    private readonly ILogger<EventWatcher> _logger;
    private readonly IPodLifeCycle _podLifeCycle;

    public EventWatcher(ILogger<EventWatcher> logger, IKubernetes kubernetes, IPodLifeCycle podLifeCycle,
        SharpConfig config)
    {
        _logger = logger;
        _kubernetes = kubernetes;
        _podLifeCycle = podLifeCycle;
        _config = config;
    }

    public Task WatchEventStream()
    {
        var listTask = _kubernetes.CoreV1.ListNamespacedPodWithHttpMessagesAsync("default", watch: true);
        listTask.Watch<V1Pod, V1PodList>(async (type, item) =>
        {
            if (item.Spec.NodeName != _config.NodeName) return;
            switch (type)
            {
                case WatchEventType.Added:
                    _logger.LogInformation("Item Added {Name} on Node {Node}", item.Name(), item.Spec.NodeName);
                    await _podLifeCycle.CreatePodAsync(item);
                    await _kubernetes.CoreV1.PatchNamespacedPodStatusAsync(
                        new V1Patch(item, V1Patch.PatchType.MergePatch), item.Name(), item.Namespace());
                    await _kubernetes.CoreV1.CreateNamespacedEventAsync(new Corev1Event(new V1ObjectReference(
                                item.ApiVersion, string.Empty, item.Kind, item.Name(),
                                item.Namespace(),
                                item.ResourceVersion(), item.Uid()),
                            new V1ObjectMeta { GenerateName = "Pod Created" }, reason: "Started",
                            reportingComponent: "virtual-kubelet", message: "pod started", type: "Normal"),
                        item.Namespace());
                    _logger.LogInformation("Item Added done...");
                    break;
                case WatchEventType.Modified:
                    _logger.LogInformation("Item Modified {Name}", item.Name());
                    await _podLifeCycle.UpdatePodAsync(item);
                    await _kubernetes.CoreV1.PatchNamespacedPodStatusAsync(
                        new V1Patch(item, V1Patch.PatchType.MergePatch), item.Name(), item.Namespace());
                    _logger.LogInformation("Publishing Pod Event");
                    await _kubernetes.CoreV1.CreateNamespacedEventAsync(new Corev1Event(new V1ObjectReference(
                                item.ApiVersion, string.Empty, item.Kind, item.Name(),
                                item.Namespace(),
                                item.ResourceVersion(), item.Uid()),
                            new V1ObjectMeta { GenerateName = "Pod Created" }, reason: "Started",
                            reportingComponent: "virtual-kubelet", message: "pod started", type: "Normal"),
                        item.Namespace());
                    _logger.LogInformation("Item Added done...");
                    break;
                case WatchEventType.Deleted:
                    _logger.LogInformation("Item Deleted {Name}", item.Name());
                    await _podLifeCycle.DeletePodAsync(item);
                    break;
                case WatchEventType.Error:
                    _logger.LogInformation("Item Error {Name}", item.Name());
                    break;
                case WatchEventType.Bookmark:
                    _logger.LogInformation("Item Bookmark {Name}", item.Name());
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
        });
        _logger.LogInformation("WatchEventStream Stopped");
        return Task.CompletedTask;
    }
}