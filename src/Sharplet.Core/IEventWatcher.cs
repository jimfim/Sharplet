using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly JsonSerializerOptions s_specHashOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SharpConfig _config;
    private readonly IKubernetes _kubernetes;
    private readonly ILogger<EventWatcher> _logger;
    private readonly IPodController _podController;
    // Per-pod hash of the last pod spec this watcher has reacted to. A watch "Modified"
    // event fires for any change to a pod, including the status subresource that
    // PodControllerService patches on its timer, so only spec changes are actionable.
    private readonly ConcurrentDictionary<string, string> _seenSpecHashes = new();

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
        Task.Run(() => _kubernetes.CoreV1.WatchListPodForAllNamespaces(
            onEvent: (type, item) => _ = HandlePodEventAsync(type, item),
            onError: e => _logger.LogError(e, "pod watch error: {Message}", e.Message)));
        _logger.LogInformation("WatchEventStream Stopped");
        return Task.CompletedTask;
    }

    internal async Task HandlePodEventAsync(WatchEventType type, V1Pod item)
    {
        if (item.Spec.NodeName != _config.NodeName) return;
        string podKey = $"{item.Namespace()}/{item.Name()}";
        switch (type)
        {
            case WatchEventType.Added:
                if (_seenSpecHashes.ContainsKey(podKey))
                {
                    // A watch reconnect re-lists every pod: ones that already started on this
                    // node are not new starts.
                    _logger.LogDebug("Item {Name} already tracked; ignoring re-listed Added event", item.Name());
                    return;
                }
                await StartPodAsync(item, podKey);
                break;
            case WatchEventType.Modified:
                string specHash = ComputeSpecHash(item);
                if (_seenSpecHashes.TryGetValue(podKey, out string? lastSeenHash) && lastSeenHash == specHash)
                {
                    // Status-only churn: this kubelet's own periodic status patch (or an
                    // equivalent write) bumped the resourceVersion. Nothing to react to.
                    _logger.LogDebug("Item {Name} status-only change; ignoring", item.Name());
                    return;
                }
                if (_seenSpecHashes.ContainsKey(podKey))
                {
                    // The pod already started on this node and its spec changed.
                    _logger.LogInformation("Item Modified {Name}", item.Name());
                    await _podController.UpdatePodAsync(item);
                    await PublishPodEventAsync(item, "Pod Updated", "Patched", "pod spec updated");
                    _seenSpecHashes[podKey] = specHash;
                }
                else
                {
                    // First time this pod shows up on this node: the scheduler assigned
                    // spec.nodeName after the pod was seen, or the watch started late.
                    await StartPodAsync(item, podKey);
                }
                break;
            case WatchEventType.Deleted:
                _logger.LogInformation("Item Deleted {Name}", item.Name());
                await _podController.DeletePodAsync(item);
                _seenSpecHashes.TryRemove(podKey, out _);
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

    private async Task StartPodAsync(V1Pod item, string podKey)
    {
        _logger.LogInformation("Item Added {Name} on Node {Node}", item.Name(), item.Spec.NodeName);
        await _podController.CreatePodAsync(item);
        await PublishPodEventAsync(item, "Pod Created", "Started", "pod started");
        _seenSpecHashes[podKey] = ComputeSpecHash(item);
    }

    private async Task PublishPodEventAsync(V1Pod item, string generateName, string reason, string message)
    {
        _logger.LogInformation("Publishing Pod Event {Reason} for {Name}", reason, item.Name());
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
            Metadata = new V1ObjectMeta { GenerateName = generateName },
            Reason = reason,
            ReportingComponent = _config.NodeName,
            Message = message,
            Type = "Normal"
        }, item.Namespace());
    }

    private static string ComputeSpecHash(V1Pod item)
    {
        byte[] specBytes = JsonSerializer.SerializeToUtf8Bytes(item.Spec, s_specHashOptions);
        byte[] specHash = SHA256.HashData(specBytes);
        return Convert.ToHexString(specHash);
    }
}
