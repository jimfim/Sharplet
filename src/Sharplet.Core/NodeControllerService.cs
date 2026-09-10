using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sharplet.Core;

public class NodeControllerService : BackgroundService
{
    private readonly SharpConfig _config;
    private readonly IKubernetes _kubernetes;
    private readonly ILogger<NodeControllerService> _logger;
    private readonly INodeController _nodeController;
    private readonly LeaderElectionService _leaderElection;

    public NodeControllerService(INodeController nodeController, IKubernetes kubernetes,
        ILogger<NodeControllerService> logger, SharpConfig config, LeaderElectionService leaderElection)
    {
        _nodeController = nodeController;
        _kubernetes = kubernetes;
        _logger = logger;
        _config = config;
        _leaderElection = leaderElection;
    }

    public override async Task<Task> StartAsync(CancellationToken cancellationToken)
    {
        await _nodeController.CreateNodeAsync(new V1Node()
        {
            Metadata = new V1ObjectMeta()
            {
                Name = _config.NodeName
            }
        }, cancellationToken);

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting status tracker");
        PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_config.NodeStatusUpdateInterval * 1000));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Only the leader holds the node lease and writes node status; followers skip
            // the tick and pick up work when they win the election.
            if (_leaderElection.IsLeader is false)
            {
                continue;
            }

            HttpOperationResponse<V1Node> node = await _kubernetes.CoreV1.ReadNodeWithHttpMessagesAsync(_config.NodeName, cancellationToken: stoppingToken);
            V1NodeStatus status = await _nodeController.GetNodeStatusAsync(_config.NodeName, stoppingToken);
            node.Body.Status = status;
            await _kubernetes.CoreV1.PatchNodeStatusAsync(new V1Patch(node.Body, V1Patch.PatchType.MergePatch), _config.NodeName, cancellationToken: stoppingToken);
        }
    }
}