using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Sharplet.Core.Tests;

/// <summary>
/// Exercises <see cref="LeaderElectionService"/> against a mocked lease API: the elector from
/// the KubernetesClient package runs for real, the API server is a substitute. Leadership state
/// (<see cref="LeaderElectionService.IsLeader"/>, <see cref="LeaderElectionService.LeaderIdentity"/>)
/// is what the node/pod status loops and the /readyz probe gate on.
/// </summary>
public class LeaderElectionServiceTests
{
    private const string NodeName = "test-node";
    private const string PodName = "test-pod";

    private static LeaderElectionService CreateService(MockCluster cluster)
    {
        return new LeaderElectionService(new SharpConfig { NodeName = NodeName }, cluster.Kubernetes,
            NullLogger<LeaderElectionService>.Instance);
    }

    [Fact]
    public async Task AcquiresLeadership_WhenLeaseIsMissing()
    {
        MockCluster cluster = MockCluster.CreateLeader();
        Environment.SetEnvironmentVariable("POD_NAME", PodName);
        try
        {
            LeaderElectionService service = CreateService(cluster);
            await service.StartAsync(CancellationToken.None);
            try
            {
                // The elector creates the missing lease and wins on its first attempt.
                await TestHelper.WaitUntilAsync(() => service.IsLeader);
                Assert.Equal(PodName, service.LeaderIdentity);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("POD_NAME", null);
        }
    }

    [Fact]
    public async Task RemainsFollower_WhenPeerHoldsFreshLease()
    {
        MockCluster cluster = MockCluster.CreateFollower("peer-1");
        LeaderElectionService service = CreateService(cluster);
        await service.StartAsync(CancellationToken.None);
        try
        {
            // The elector observes the peer as leader but must never preempt a fresh lease.
            await TestHelper.WaitUntilAsync(() => service.LeaderIdentity == "peer-1");
            Assert.False(service.IsLeader);
            await Task.Delay(1000, TestContext.Current.CancellationToken);
            Assert.False(service.IsLeader);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task CompletesLegacyLeaseRecord_MissingAcquireTime()
    {
        ICoreV1Operations coreV1 = Substitute.For<ICoreV1Operations>();
        coreV1.CreateNamespaceWithHttpMessagesAsync(
                Arg.Any<V1Namespace>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<HttpOperationResponse<V1Namespace>>(
                TestHelper.ApiException(HttpStatusCode.Conflict, "namespace already exists")));
        ICoordinationV1Operations coordinationV1 = Substitute.For<ICoordinationV1Operations>();
        DateTime now = DateTime.UtcNow;
        // A pre-leader-election lease: holder is recorded but acquireTime is missing, which the
        // elector treats as an incomplete record it would never take over.
        V1Lease legacyLease = new()
        {
            Metadata = new V1ObjectMeta { Name = NodeName, NamespaceProperty = LeaderElectionService.LeaseNamespace },
            Spec = new V1LeaseSpec
            {
                HolderIdentity = "legacy",
                RenewTime = now.AddSeconds(-1),
                LeaseDurationSeconds = 40,
            },
        };
        coordinationV1.ReadNamespacedLeaseWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HttpOperationResponse<V1Lease> { Body = legacyLease }));
        List<V1Lease> replacedLeases = new();
        coordinationV1.ReplaceNamespacedLeaseWithHttpMessagesAsync(
                Arg.Do<V1Lease>(lease => replacedLeases.Add(lease)), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new HttpOperationResponse<V1Lease> { Body = ci.ArgAt<V1Lease>(0) }));
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        kubernetes.CoreV1.Returns(coreV1);
        kubernetes.CoordinationV1.Returns(coordinationV1);

        LeaderElectionService service =
            new(new SharpConfig { NodeName = NodeName }, kubernetes, NullLogger<LeaderElectionService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            // The missing acquireTime is filled in before the elector runs so the standard
            // expiry-based takeover can work off the record.
            await TestHelper.WaitUntilAsync(() => replacedLeases.Count > 0);
            V1LeaseSpec completed = replacedLeases[0].Spec;
            Assert.NotNull(completed.AcquireTime);
            Assert.NotNull(completed.RenewTime);
            Assert.False(string.IsNullOrEmpty(completed.HolderIdentity));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task KeepsRunning_WhenApiIsUnavailable()
    {
        ICoreV1Operations coreV1 = Substitute.For<ICoreV1Operations>();
        List<V1Namespace> namespaceCreateAttempts = new();
        coreV1.CreateNamespaceWithHttpMessagesAsync(
                Arg.Do<V1Namespace>(namespace_ => namespaceCreateAttempts.Add(namespace_)), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<HttpOperationResponse<V1Namespace>>(new Exception("api server unreachable")));
        ICoordinationV1Operations coordinationV1 = Substitute.For<ICoordinationV1Operations>();
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        kubernetes.CoreV1.Returns(coreV1);
        kubernetes.CoordinationV1.Returns(coordinationV1);

        LeaderElectionService service =
            new(new SharpConfig { NodeName = NodeName }, kubernetes, NullLogger<LeaderElectionService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            // The namespace lookup fails; the service must log and retry instead of stopping the host.
            await TestHelper.WaitUntilAsync(() => namespaceCreateAttempts.Count > 0);
            Assert.False(service.IsLeader);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}