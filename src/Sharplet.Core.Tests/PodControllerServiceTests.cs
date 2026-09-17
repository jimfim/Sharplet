using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Sharplet.Core.Tests;

/// <summary>
/// Exercises <see cref="PodControllerService"/> against a mocked API server with a real
/// <see cref="LeaderElectionService"/>. The leader path lists the node's pods (field
/// selector on <c>spec.nodeName</c>) and patches each pod's status subresource from the
/// provider; one bad pod must not block the rest, a failed list call backs off, and a
/// follower makes no API calls at all.
/// </summary>
public class PodControllerServiceTests
{
    private const string NodeName = "test-node";

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture(MockCluster cluster, IPodController podController, LeaderElectionService leader,
            PodControllerService service)
        {
            Cluster = cluster;
            PodController = podController;
            Leader = leader;
            Service = service;
        }

        public MockCluster Cluster { get; }
        public IPodController PodController { get; }
        public LeaderElectionService Leader { get; }
        public PodControllerService Service { get; }

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            await Leader.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<Fixture> CreateFixture(MockCluster cluster, IPodController podController)
    {
        SharpConfig config = new() { NodeName = NodeName, PodStatusUpdateInterval = 1 };
        LeaderElectionService leader =
            new(config, cluster.Kubernetes, NullLogger<LeaderElectionService>.Instance);
        await leader.StartAsync(CancellationToken.None);
        PodControllerService service =
            new(config, podController, NullLogger<PodControllerService>.Instance, cluster.Kubernetes, leader);
        await service.StartAsync(CancellationToken.None);
        return new Fixture(cluster, podController, leader, service);
    }

    private static V1Pod CreatePod(string name)
    {
        return new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = "default" },
            Spec = new V1PodSpec { NodeName = NodeName },
        };
    }

    private static void MockPodList(MockCluster cluster, IEnumerable<V1Pod> pods, List<string> fieldSelectors)
    {
        cluster.CoreV1.ListNamespacedPodWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Do<string>(selector => fieldSelectors.Add(selector)),
                Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HttpOperationResponse<V1PodList>
            {
                Body = new V1PodList { Items = pods.ToList() },
            }));
    }

    [Fact]
    public async Task Leader_PatchesProviderStatusForEveryPod()
    {
        MockCluster cluster = MockCluster.CreateLeader();
        V1Pod podB = CreatePod("pod-b");
        V1Pod podD = CreatePod("pod-d");
        List<string> fieldSelectors = new();
        MockPodList(cluster, new[] { CreatePod("pod-a"), podB, CreatePod("pod-c"), podD }, fieldSelectors);

        List<string> patchedPodNames = new();
        cluster.CoreV1.PatchNamespacedPodStatusWithHttpMessagesAsync(
                Arg.Any<V1Patch>(), Arg.Do<string>(name => patchedPodNames.Add(name)), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HttpOperationResponse<V1Pod>()));
        // pod-c's status patch fails: the loop must keep going and still patch pod-d.
        cluster.CoreV1.PatchNamespacedPodStatusWithHttpMessagesAsync(
                Arg.Any<V1Patch>(), "pod-c", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<HttpOperationResponse<V1Pod>>(new Exception("patch failed")));

        IPodController podController = Substitute.For<IPodController>();
        // pod-a has no status to report: it is skipped, not patched.
        podController.GetPodStatusAsync("default", "pod-a", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<V1PodStatus>(null!));
        podController.GetPodStatusAsync("default", "pod-b", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new V1PodStatus { Phase = "Running" }));
        podController.GetPodStatusAsync("default", "pod-c", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new V1PodStatus { Phase = "Running" }));
        podController.GetPodStatusAsync("default", "pod-d", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new V1PodStatus { Phase = "Pending" }));

        string? previousPodNamespace = Environment.GetEnvironmentVariable("POD_NAMESPACE");
        Environment.SetEnvironmentVariable("POD_NAMESPACE", null);
        try
        {
            await using Fixture fixture = await CreateFixture(cluster, podController);

            await TestHelper.WaitUntilAsync(() => fixture.Leader.IsLeader);
            await TestHelper.WaitUntilAsync(() => patchedPodNames.Contains("pod-d"));

            // POD_NAMESPACE is unset: the default namespace is listed, scoped to this node.
            Assert.Equal("spec.nodeName=" + NodeName, Assert.Single(fieldSelectors));
            Assert.Contains("pod-b", patchedPodNames);
            Assert.Contains("pod-c", patchedPodNames);
            Assert.Contains("pod-d", patchedPodNames);
            Assert.DoesNotContain("pod-a", patchedPodNames);
        }
        finally
        {
            Environment.SetEnvironmentVariable("POD_NAMESPACE", previousPodNamespace);
        }
    }

    [Fact]
    public async Task ListFailure_ServiceKeepsRunning()
    {
        MockCluster cluster = MockCluster.CreateLeader();
        cluster.CoreV1.ListNamespacedPodWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<HttpOperationResponse<V1PodList>>(new Exception("api server unreachable")));

        RecordingLogger<PodControllerService> logger = new();
        SharpConfig config = new() { NodeName = NodeName, PodStatusUpdateInterval = 1 };
        LeaderElectionService leader =
            new(config, cluster.Kubernetes, NullLogger<LeaderElectionService>.Instance);
        await leader.StartAsync(CancellationToken.None);
        PodControllerService service =
            new(config, Substitute.For<IPodController>(), logger, cluster.Kubernetes, leader);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await TestHelper.WaitUntilAsync(() => leader.IsLeader);
            // The failed list is logged with the backoff note; the service keeps looping.
            await TestHelper.WaitUntilAsync(() => logger.Entries.Any(entry => entry.Level == LogLevel.Error));
            Assert.Contains(logger.Entries, entry => entry.Message.Contains("pod list failed"));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await leader.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Follower_MakesNoApiCalls()
    {
        MockCluster cluster = MockCluster.CreateFollower("peer-1");
        MockPodList(cluster, Array.Empty<V1Pod>(), new());
        IPodController podController = Substitute.For<IPodController>();
        await using Fixture fixture = await CreateFixture(cluster, podController);

        // Two full ticks (1s interval) while the peer holds the lease.
        await TestHelper.WaitUntilAsync(() => fixture.Leader.LeaderIdentity == "peer-1");
        await Task.Delay(2500, TestContext.Current.CancellationToken);

        _ = cluster.CoreV1.DidNotReceiveWithAnyArgs().ListNamespacedPodWithHttpMessagesAsync(
            Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<int?>(),
            Arg.Any<bool?>(), Arg.Any<bool?>(),
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());
        _ = podController.DidNotReceiveWithAnyArgs().GetPodStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.False(fixture.Leader.IsLeader);
    }

    [Fact]
    public async Task ListCanceled_StopsTheService()
    {
        // A cancellation surfaced from the pod list call (the host is shutting down) must
        // stop the status loop: no further ticks, no status patches.
        MockCluster cluster = MockCluster.CreateLeader();
        List<string> fieldSelectors = new();
        cluster.CoreV1.ListNamespacedPodWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Do<string>(selector => fieldSelectors.Add(selector)),
                Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<HttpOperationResponse<V1PodList>>(new OperationCanceledException("shutting down")));

        IPodController podController = Substitute.For<IPodController>();
        podController.GetPodStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new V1PodStatus { Phase = "Running" }));

        string? previousPodNamespace = Environment.GetEnvironmentVariable("POD_NAMESPACE");
        Environment.SetEnvironmentVariable("POD_NAMESPACE", null);
        try
        {
            await using Fixture fixture = await CreateFixture(cluster, podController);

            await TestHelper.WaitUntilAsync(() => fixture.Leader.IsLeader);
            await TestHelper.WaitUntilAsync(() => fieldSelectors.Count > 0);
            await Task.Delay(1500, TestContext.Current.CancellationToken);
            // The loop stopped after the cancelled list: the next tick (1s) never ran.
            Assert.Single(fieldSelectors);
            await podController.DidNotReceive().GetPodStatusAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("POD_NAMESPACE", previousPodNamespace);
        }
    }
}