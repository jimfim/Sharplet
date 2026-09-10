using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sharplet.Core;

public class PodControllerService : BackgroundService
{
    private readonly SharpConfig _config;
    private readonly IKubernetes _kubernetes;
    private readonly ILogger<PodControllerService> _logger;
    private readonly IPodController _podController;
    private readonly string _podNamespace;

    public PodControllerService(SharpConfig config, IPodController podController,
        ILogger<PodControllerService> logger, IKubernetes kubernetes)
    {
        _config = config;
        _podController = podController;
        _logger = logger;
        _kubernetes = kubernetes;
        _podNamespace = Environment.GetEnvironmentVariable("POD_NAMESPACE") ?? "default";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_config.PodStatusUpdateInterval * 1000));
        _logger.LogInformation("Starting status tracker");

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Only patch pods scheduled onto this node: the scheduler sets spec.nodeName,
            // so a field selector keeps pods on real nodes out of the patch loop.
            HttpOperationResponse<V1PodList> apiPods =
                await _kubernetes.CoreV1.ListNamespacedPodWithHttpMessagesAsync(_podNamespace,
                    fieldSelector: $"spec.nodeName={_config.NodeName}",
                    cancellationToken: stoppingToken);

            foreach (V1Pod pod in apiPods.Body.Items)
            {
                V1PodStatus status =
                    await _podController.GetPodStatusAsync(pod.Namespace(), pod.Name(), stoppingToken);
                if (status is null)
                {
                    continue;
                }

                _logger.LogInformation("tracker updating pod {PodName}", pod.Name());
                // Patch the pods/status subresource from the provider's status; the API
                // server applies only the status portion of the merge patch.
                pod.Status = status;
                await _kubernetes.CoreV1.PatchNamespacedPodStatusAsync(
                    new V1Patch(pod, V1Patch.PatchType.MergePatch), pod.Name(), pod.Namespace(),
                    cancellationToken: stoppingToken);
            }
        }
    }
}
