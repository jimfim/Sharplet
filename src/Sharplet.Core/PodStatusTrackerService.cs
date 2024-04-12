using k8s;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sharplet.Abstractions;

namespace Sharplet.Core;

public class PodStatusTrackerService : BackgroundService
{
    private readonly SharpConfig _config;
    private readonly IKubernetes _kubernetes;
    private readonly ILogger<PodStatusTrackerService> _logger;
    private readonly IPodLifeCycle _podLifeCycle;

    public PodStatusTrackerService(SharpConfig config, IPodLifeCycle podLifeCycle,
        ILogger<PodStatusTrackerService> logger, IKubernetes kubernetes)
    {
        _config = config;
        _podLifeCycle = podLifeCycle;
        _logger = logger;
        _kubernetes = kubernetes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_config.StatusUpdateInterval * 1000));
        _logger.LogInformation("Starting status tracker");

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var apiPods =
                await _kubernetes.CoreV1.ListNamespacedPodWithHttpMessagesAsync("default",
                    cancellationToken: stoppingToken);
            _logger.LogInformation("tracker updating");
            //var pods = await _podLifeCycle.GetPodsAsync(stoppingToken);
            // if (pods.IsNullOrEmpty())
            // {
            //     continue;
            // }

            foreach (var pod in apiPods.Body)
            {
                var status = await _podLifeCycle.GetPodStatusAsync(pod.Namespace(), pod.Name(), stoppingToken);
                var apipod = apiPods.Body.Items.FirstOrDefault(x => x.Name() == pod.Name());
                if (apipod == null) continue;
                apipod.Status = status;
                _logger.LogInformation("tracker updating pod {PodName}", pod.Name());
                await _kubernetes.CoreV1.PatchNamespacedPodStatusAsync(
                    new V1Patch(apipod, V1Patch.PatchType.MergePatch), pod.Name(), pod.Namespace(),
                    cancellationToken: stoppingToken);
            }
        }
    }
}