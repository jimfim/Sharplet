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

    /// <summary>
    /// The identity of the current leader of the node lease — this instance or a peer.
    /// Null until the lease has been observed for the first time.
    /// </summary>
    public string? LeaderIdentity => _elector?.GetLeader();

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
        await EnsureLeaseRecordCompleteAsync(stoppingToken);
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

    private async Task EnsureLeaseRecordCompleteAsync(CancellationToken cancellationToken)
    {
        V1Lease lease;
        try
        {
            lease = await _kubernetes.CoordinationV1.ReadNamespacedLeaseAsync(_config.NodeName, LeaseNamespace, cancellationToken: cancellationToken);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return; // no lease yet; the elector creates it
        }

        if (lease.Spec.AcquireTime is not null)
        {
            return; // record is complete; nothing to fix
        }

        // Legacy leases (e.g. created by a pre-leader-election sharplet) may lack
        // acquireTime. The elector treats such a record as incomplete and only knows how
        // to create it, so it would never take over. Fill in the missing fields so the
        // standard expiry-based takeover works.
        DateTime now = DateTime.UtcNow;
        lease.Spec.AcquireTime = now;
        lease.Spec.RenewTime ??= now;
        lease.Spec.HolderIdentity ??= _config.NodeName;
        try
        {
            await _kubernetes.CoordinationV1.ReplaceNamespacedLeaseAsync(lease, _config.NodeName, LeaseNamespace, cancellationToken: cancellationToken);
            _logger.LogInformation("completed lease record for {LeaseName} in {Namespace}", _config.NodeName, LeaseNamespace);
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.Conflict)
        {
            // a replica raced us; the record is complete now anyway
        }
    }
}