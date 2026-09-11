using System.Net;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting status tracker");
        PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_config.NodeStatusUpdateInterval * 1000));
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

            // Only the leader holds the node lease and writes node status; followers skip
            // the tick and pick up work when they win the election.
            if (_leaderElection.IsLeader is false)
            {
                continue;
            }

            try
            {
                // A transient API error must not stop the host: the node keeps its last
                // reported status while the API is unreachable, and the control plane's
                // node monitor marks it NotReady once heartbeats go stale.
                HttpOperationResponse<V1Node> node =
                    await _kubernetes.CoreV1.ReadNodeWithHttpMessagesAsync(_config.NodeName, cancellationToken: stoppingToken);
                V1NodeStatus status = await _nodeController.GetNodeStatusAsync(_config.NodeName, stoppingToken);
                node.Body.Status = status;
                await _kubernetes.CoreV1.PatchNodeStatusAsync(new V1Patch(node.Body, V1Patch.PatchType.MergePatch), _config.NodeName, cancellationToken: stoppingToken);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (HttpOperationException e) when (e.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                // The node object is missing (e.g. first boot ran before the API was
                // reachable, or the control plane deleted it): ask the provider to
                // (re)create it and report its status on the next tick.
                _logger.LogInformation("node {NodeName} not found; asking the provider to create it", _config.NodeName);
                try
                {
                    await _nodeController.CreateNodeAsync(new V1Node()
                    {
                        Metadata = new V1ObjectMeta()
                        {
                            Name = _config.NodeName
                        }
                    }, stoppingToken);
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    consecutiveFailures++;
                    _logger.LogError(exception, "node {NodeName} recreation failed; retrying after backoff", _config.NodeName);
                    await Task.Delay(GetBackoff(consecutiveFailures), stoppingToken);
                }
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                _logger.LogError(exception, "node status update failed; retrying after backoff ({ConsecutiveFailures} consecutive failures)", consecutiveFailures);
                await Task.Delay(GetBackoff(consecutiveFailures), stoppingToken);
            }
        }
    }

    // Linear backoff (10s per consecutive failure, capped at 60s) keeps a struggling API
    // from being hammered on every tick while the node degrades in the background.
    private static TimeSpan GetBackoff(int consecutiveFailures) =>
        TimeSpan.FromSeconds(Math.Min(consecutiveFailures * 10, 60));
}