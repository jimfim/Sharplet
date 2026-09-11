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
    private readonly LeaderElectionService _leaderElection;

    public PodControllerService(SharpConfig config, IPodController podController,
        ILogger<PodControllerService> logger, IKubernetes kubernetes, LeaderElectionService leaderElection)
    {
        _config = config;
        _podController = podController;
        _logger = logger;
        _kubernetes = kubernetes;
        _podNamespace = Environment.GetEnvironmentVariable("POD_NAMESPACE") ?? "default";
        _leaderElection = leaderElection;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_config.PodStatusUpdateInterval * 1000));
        _logger.LogInformation("Starting status tracker");
        int consecutiveFailures = 0;

        while (true)
        {
            try
            {
                if (await timer.WaitForNextTickAsync(stoppingToken) is false)
                {
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                return; // shutting down
            }

            // Only the leader patches pod status; followers wait for a takeover.
            if (_leaderElection.IsLeader is false)
            {
                continue;
            }

            HttpOperationResponse<V1PodList> apiPods;
            try
            {
                // Only patch pods scheduled onto this node: the scheduler sets spec.nodeName,
                // so a field selector keeps pods on real nodes out of the patch loop.
                apiPods =
                    await _kubernetes.CoreV1.ListNamespacedPodWithHttpMessagesAsync(_podNamespace,
                        fieldSelector: $"spec.nodeName={_config.NodeName}",
                        cancellationToken: stoppingToken);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                _logger.LogError(exception, "pod list failed; retrying after backoff ({ConsecutiveFailures} consecutive failures)", consecutiveFailures);
                await Task.Delay(GetBackoff(consecutiveFailures), stoppingToken);
                continue;
            }

            foreach (V1Pod pod in apiPods.Body.Items)
            {
                try
                {
                    V1PodStatus? status = await _podController.GetPodStatusAsync(pod.Namespace(), pod.Name(), stoppingToken);
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
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    // One bad pod must not block the rest: log it and keep patching.
                    _logger.LogWarning(exception, "failed to update status for pod {Namespace}/{PodName}", pod.Namespace(), pod.Name());
                }
            }
        }
    }

    // Linear backoff (10s per consecutive failure, capped at 60s) keeps a struggling API
    // from being hammered on every tick while pod status degrades in the background.
    private static TimeSpan GetBackoff(int consecutiveFailures) =>
        TimeSpan.FromSeconds(Math.Min(consecutiveFailures * 10, 60));
}
