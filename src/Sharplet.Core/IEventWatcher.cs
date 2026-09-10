using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

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
    private readonly IPodController _podController;

    public EventWatcher(ILogger<EventWatcher> logger, IKubernetes kubernetes, IPodController podController,
        SharpConfig config)
    {
        _logger = logger;
        _kubernetes = kubernetes;
        _podController = podController;
        _config = config;
    }

    public Task WatchEventStream()
    {
        Task.Run(() => _kubernetes.CoreV1.WatchListNamespacedPod("default",
            onEvent: (type, item) => _ = HandlePodEventAsync(type, item),
            onError: e => _logger.LogError(e, "pod watch error: {Message}", e.Message)));
        _logger.LogInformation("WatchEventStream Stopped");
        return Task.CompletedTask;
    }

    private async Task HandlePodEventAsync(WatchEventType type, V1Pod item)
    {
        if (item.Spec.NodeName != _config.NodeName) return;
        switch (type)
        {
            case WatchEventType.Added:
                _logger.LogInformation("Item Added {Name} on Node {Node}", item.Name(), item.Spec.NodeName);
                await _podController.CreatePodAsync(item);
                // await _kubernetes.CoreV1.PatchNamespacedPodStatusAsync(
                //     new V1Patch(item, V1Patch.PatchType.MergePatch), item.Name(), item.Namespace());
                await _kubernetes.CoreV1.CreateNamespacedEventAsync(new Corev1Event
                {
                    InvolvedObject = new V1ObjectReference
                    {
                        ApiVersion = item.ApiVersion,
                        FieldPath = string.Empty,
                        Kind = item.Kind,
                        Name = item.Name(),
                        NamespaceProperty = item.Namespace(),
                        ResourceVersion = item.ResourceVersion(),
                        Uid = item.Uid()
                    },
                    Metadata = new V1ObjectMeta { GenerateName = "Pod Created" },
                    Reason = "Started",
                    ReportingComponent = _config.NodeName,
                    Message = "pod started",
                    Type = "Normal"
                }, item.Namespace());
                _logger.LogInformation("Item Added done...");
                break;
            case WatchEventType.Modified:
                _logger.LogInformation("Item Modified {Name}", item.Name());
                await _podController.UpdatePodAsync(item);
                // await _kubernetes.CoreV1.PatchNamespacedPodStatusAsync(
                //     new V1Patch(item, V1Patch.PatchType.MergePatch), item.Name(), item.Namespace());
                _logger.LogInformation("Publishing Pod Event");
                await _kubernetes.CoreV1.CreateNamespacedEventAsync(new Corev1Event
                {
                    InvolvedObject = new V1ObjectReference
                    {
                        ApiVersion = item.ApiVersion,
                        FieldPath = string.Empty,
                        Kind = item.Kind,
                        Name = item.Name(),
                        NamespaceProperty = item.Namespace(),
                        ResourceVersion = item.ResourceVersion(),
                        Uid = item.Uid()
                    },
                    Metadata = new V1ObjectMeta { GenerateName = "Pod Created" },
                    Reason = "Started",
                    ReportingComponent = _config.NodeName,
                    Message = "pod started",
                    Type = "Normal"
                }, item.Namespace());
                _logger.LogInformation("Item Added done...");
                break;
            case WatchEventType.Deleted:
                _logger.LogInformation("Item Deleted {Name}", item.Name());
                await _podController.DeletePodAsync(item);
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
    }
}
