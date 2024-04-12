using k8s;
using k8s.LeaderElection;
using k8s.LeaderElection.ResourceLock;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sharplet.Core;

public class NodeControllerService : BackgroundService
{
    private readonly SharpConfig _config;
    private readonly IKubernetes _kubernetes;
    private readonly ILogger _logger;
    private readonly INodeController _nodeController;

    public NodeControllerService(INodeController nodeController, IKubernetes kubernetes,
        ILogger<NodeControllerService> logger, SharpConfig config)
    {
        _nodeController = nodeController;
        _kubernetes = kubernetes;
        _logger = logger;
        _config = config;
    }

    public override async Task<Task> StartAsync(CancellationToken cancellationToken)
    {
        await _nodeController.RegisterNodeAsync(_config.NodeName);
        //PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_config.StatusUpdateInterval * 1000));
        // _logger.LogInformation("Starting status tracker");
        //
        // while (await timer.WaitForNextTickAsync(cancellationToken))
        // {
        //     var node = await _kubernetes.CoreV1.ReadNodeWithHttpMessagesAsync(_config.NodeName, cancellationToken: cancellationToken);
        //     var status = await _nodeController.GetNodeStatusAsync(_config.NodeName);
        //     node.Body.Status = status;
        //     await _kubernetes.CoreV1.PatchNodeStatusAsync(new V1Patch(node.Body, V1Patch.PatchType.MergePatch), _config.NodeName, cancellationToken: cancellationToken);
        // }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // var leaseName = _config.NodeName;
        // var leaseLock = new LeaseLock(_kubernetes, "kube-node-lease", leaseName, leaseName);
        // var config = new LeaderElectionConfig(leaseLock);
        // var elector = new LeaderElector(config);
        // elector.OnNewLeader += s => _logger.LogInformation("Leader Elected {Leader}", s);
        // elector.OnStartedLeading += () => _logger.LogInformation("OnStartedLeading");
        // elector.OnStoppedLeading += () => _logger.LogInformation("OnStoppedLeading");
        // //elector.OnError += () => _logger.LogInformation("OnError");
        // await elector.RunUntilLeadershipLostAsync(stoppingToken);
        
        _logger.LogInformation("Starting status tracker");
        PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_config.StatusUpdateInterval * 1000));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var node = await _kubernetes.CoreV1.ReadNodeWithHttpMessagesAsync(_config.NodeName, cancellationToken: stoppingToken);
            var status = await _nodeController.GetNodeStatusAsync(_config.NodeName);
            node.Body.Status = status;
            await _kubernetes.CoreV1.PatchNodeStatusAsync(new V1Patch(node.Body, V1Patch.PatchType.MergePatch), _config.NodeName, cancellationToken: stoppingToken);
        }
    }

    public override async Task<Task> StopAsync(CancellationToken cancellationToken)
    {
        //await _nodeController.RemoveNodeAsync(_config.NodeName);
        return base.StopAsync(cancellationToken);
    }
}