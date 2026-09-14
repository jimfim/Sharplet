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
    private readonly string _identity;
    private LeaderElector? _elector;

    public LeaderElectionService(SharpConfig config, IKubernetes kubernetes, ILogger<LeaderElectionService> logger)
    {
        _config = config;
        _kubernetes = kubernetes;
        _logger = logger;
        // One identity per replica (POD_NAME in-cluster, a per-process GUID outside) so a
        // restarted pod never keeps holding the lease under a stale identity.
        _identity = Environment.GetEnvironmentVariable("POD_NAME") ?? $"{config.NodeName}-{Guid.NewGuid():N}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                LeaseLock leaseLock = new(_kubernetes, LeaseNamespace, _config.NodeName, _identity);
                LeaderElectionConfig electionConfig = new(leaseLock)
                {
                    LeaseDuration = LeaseDuration,
                    RenewDeadline = RenewDeadline,
                    RetryPeriod = RetryPeriod
                };
                LeaderElector elector = new(electionConfig);
                elector.OnNewLeader += LogNewLeader;
                elector.OnStartedLeading += LogLeadershipAcquired;
                elector.OnStoppedLeading += LogLeadershipLost;
                elector.OnError += HandleElectionError;

                _elector = elector;
                await EnsureLeaseNamespaceAsync(stoppingToken);
                await EnsureLeaseRecordCompleteAsync(stoppingToken);
                await elector.RunAndTryToHoldLeadershipForeverAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                // The API may be down at boot or the lease record broken: log and retry
                // instead of stopping the whole host; the replica simply does not lead.
                _logger.LogWarning(exception, "leader election for node {NodeName} failed; retrying", _config.NodeName);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private void LogNewLeader(string leader)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("node lease {LeaseName} in {Namespace} held by {Leader}", _config.NodeName, LeaseNamespace, leader);
        }
    }

    private void LogLeadershipAcquired()
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("sharplet {Identity} acquired leadership for node {NodeName}", _identity, _config.NodeName);
        }
    }

    private void LogLeadershipLost()
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("sharplet {Identity} stopped leading for node {NodeName}", _identity, _config.NodeName);
        }
    }

    private void HandleElectionError(Exception error)
    {
        if (error is OperationCanceledException)
        {
            return; // shutting down
        }
        _logger.LogWarning(error, "leader election for node {NodeName} hit an error; retrying", _config.NodeName);
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
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("created namespace {Namespace}", LeaseNamespace);
            }
        }
        catch (HttpOperationException e) when (e.Response?.StatusCode == HttpStatusCode.Conflict)
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
        catch (HttpOperationException e) when (e.Response?.StatusCode == HttpStatusCode.NotFound)
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
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("completed lease record for {LeaseName} in {Namespace}", _config.NodeName, LeaseNamespace);
            }
        }
        catch (HttpOperationException e) when (e.Response?.StatusCode == HttpStatusCode.Conflict)
        {
            // a replica raced us; the record is complete now anyway
        }
    }
}