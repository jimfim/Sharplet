using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Sharplet.Core;

/// <summary>
/// Writes provider operation failures (a thrown <see cref="IPodController.CreatePodAsync"/> or
/// <see cref="IPodController.UpdatePodAsync"/>) back to the API server so a pod whose backend
/// failed to start is not left stuck in Pending with no signal. Mirrors virtual-kubelet's
/// handleProviderError: the pod's status subresource is patched with <c>reason:
/// ProviderFailed</c>, the provider error as message, and a phase of Pending (or Failed when
/// the pod's restart policy is Never), and a Warning event (ProviderCreateFailed /
/// ProviderUpdateFailed) is recorded on the pod.
/// </summary>
internal sealed class PodErrorReporter
{
    private readonly SharpConfig _config;
    private readonly IKubernetes _kubernetes;
    private readonly ILogger<PodErrorReporter> _logger;

    public PodErrorReporter(SharpConfig config, IKubernetes kubernetes, ILogger<PodErrorReporter> logger)
    {
        _config = config;
        _kubernetes = kubernetes;
        _logger = logger;
    }

    /// <summary>
    /// Patches the pod's status subresource to report the provider failure and records a
    /// Warning event on the pod. A pod that already reached a terminal phase keeps that
    /// status: a late failure must not flip a Succeeded or Failed pod back to Pending.
    /// </summary>
    /// <param name="pod">The pod the provider operation failed for.</param>
    /// <param name="exception">The exception the provider threw.</param>
    /// <param name="operation">"create" or "update"; selects the event reason.</param>
    public async Task ReportProviderErrorAsync(V1Pod pod, Exception exception, string operation,
        CancellationToken cancellationToken = default)
    {
        if (pod.Status is { Phase: "Succeeded" or "Failed" })
        {
            return;
        }

        // Capture the event (with the pod's current resourceVersion in the involved object)
        // before the status rewrite below blanks it.
        Corev1Event errorEvent = PodEventFactory.CreatePodEvent(pod, "provider-error",
            operation == "create" ? "ProviderCreateFailed" : "ProviderUpdateFailed",
            exception.Message, "Warning", _config.NodeName);

        // Blank the resource version (virtual-kubelet parity) so a stale revision can never
        // surface as a conflict on the status write; fill the failure fields.
        pod.Metadata.ResourceVersion = string.Empty;
        V1PodStatus status = pod.Status ?? new V1PodStatus();
        status.Phase = pod.Spec?.RestartPolicy == "Never" ? "Failed" : "Pending";
        status.Reason = "ProviderFailed";
        status.Message = exception.Message;
        pod.Status = status;

        try
        {
            await _kubernetes.CoreV1.PatchNamespacedPodStatusAsync(
                new V1Patch(pod, V1Patch.PatchType.MergePatch), pod.Name(), pod.Namespace(),
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception patchException)
        {
            // The status write is best-effort: the event below is what kubectl surfaces.
            _logger.LogError(patchException,
                "failed to patch status for pod {Namespace}/{PodName} after provider {Operation} failure",
                pod.Namespace(), pod.Name(), operation);
        }

        try
        {
            await _kubernetes.CoreV1.CreateNamespacedEventAsync(errorEvent, pod.Namespace(),
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception eventException)
        {
            _logger.LogError(eventException,
                "failed to record provider error event for pod {Namespace}/{PodName}", pod.Namespace(), pod.Name());
        }
    }
}