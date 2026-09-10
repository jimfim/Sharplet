using System.Net;
using k8s;
using k8s.Autorest;
using k8s.LeaderElection;
using k8s.LeaderElection.ResourceLock;
using k8s.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sharplet.Core;

/// <summary>
/// Runs leader election over the node's lease (<c>kube-node-lease</c>, named after the node) —
/// the same lease a real kubelet publishes as the node's liveness signal. Replicas race for it
/// and only the holder (<see cref="IsLeader"/>) runs the node and pod status loops. The lease
/// doubles as the liveness signal: if the leader stops renewing for <see cref="LeaseDuration"/>
/// (40s) another replica takes over and the node stays Ready.
/// </summary>
public class LeaderElectionService : BackgroundService
{
    public const string LeaseNamespace = "kube-node-lease";

    /// <summary>
    /// True while this instance holds the node lease; the node and pod status loops run
    /// only for the leader.
    /// </summary>
    public bool IsLeader => _elector?.IsLeader() ?? false;

    // The kubelet's lease defaults (renew every 10s, node considered down 40s after the last
    // renewal) keep the lease valid as the node's liveness signal.
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan RenewDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryPeriod = TimeSpan.FromSeconds(10);

    private readonly SharpConfig _config;
    private readonly IKubernetes _kubernetes;
    private readonly ILogger<LeaderElectionService> _logger;
    private LeaderElector? _elector;

    public LeaderElectionService(SharpConfig config, IKubernetes kubernetes, ILogger<LeaderElectionService> logger)
    {
        _config = config;
        _kubernetes = kubernetes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One identity per replica (POD_NAME in-cluster, a per-process GUID outside) so a
        // restarted pod never keeps holding the lease under a stale identity.
        string identity = Environment.GetEnvironmentVariable("POD_NAME") ?? $"{_config.NodeName}-{Guid.NewGuid():N}";

        LeaseLock leaseLock = new(_kubernetes, LeaseNamespace, _config.NodeName, identity);
        LeaderElectionConfig electionConfig = new(leaseLock)
        {
            LeaseDuration = LeaseDuration,
            RenewDeadline = RenewDeadline,
            RetryPeriod = RetryPeriod
        };
        LeaderElector elector = new(electionConfig);
        elector.OnNewLeader += leader => _logger.LogInformation("node lease {LeaseName} in {Namespace} held by {Leader}", _config.NodeName, LeaseNamespace, leader);
        elector.OnStartedLeading += () => _logger.LogInformation("sharplet {Identity} acquired leadership for node {NodeName}", identity, _config.NodeName);
        elector.OnStoppedLeading += () => _logger.LogInformation("sharplet {Identity} stopped leading for node {NodeName}", identity, _config.NodeName);
        elector.OnError += error =>
        {
            if (error is OperationCanceledException)
            {
                return; // shutting down
            }
            _logger.LogWarning(error, "leader election for node {NodeName} hit an error; retrying", _config.NodeName);
        };

        _elector = elector;
        await EnsureLeaseNamespaceAsync(stoppingToken);
        await elector.RunAndTryToHoldLeadershipForeverAsync(stoppingToken);
    }

    private async Task EnsureLeaseNamespaceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _kubernetes.CoreV1.CreateNamespaceAsync(
                new V1Namespace
                {
                    Metadata = new V1ObjectMeta
                    {
                        Name = LeaseNamespace
                    }
                },
                cancellationToken: cancellationToken);
            _logger.LogInformation("created namespace {Namespace}", LeaseNamespace);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.Conflict)
        {
            // namespace already exists; nothing to do
        }
    }
}