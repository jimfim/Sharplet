using k8s;
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

    public PodControllerService(SharpConfig config, IPodController podController,
        ILogger<PodControllerService> logger, IKubernetes kubernetes)
    {
        _config = config;
        _podController = podController;
        _logger = logger;
        _kubernetes = kubernetes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_config.PodStatusUpdateInterval * 1000));
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
                var status = await _podController.GetPodStatusAsync(pod.Namespace(), pod.Name(), stoppingToken);
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