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
/// Exercises <see cref="NodeControllerService"/> against a mocked API server with a real
/// <see cref="LeaderElectionService"/> (the service depends on the concrete type, and its
/// lease-based leadership is exactly what gates the status loop). Covers the leader path
/// (read node, patch provider status), the 404 path (provider asked to recreate the node),
/// failure backoff, and the follower path (no API calls at all).
/// </summary>
public class NodeControllerServiceTests
{
    private const string NodeName = "test-node";

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture(MockCluster cluster, INodeController nodeController, LeaderElectionService leader,
            NodeControllerService service)
        {
            Cluster = cluster;
            NodeController = nodeController;
            Leader = leader;
            Service = service;
        }

        public MockCluster Cluster { get; }
        public INodeController NodeController { get; }
        public LeaderElectionService Leader { get; }
        public NodeControllerService Service { get; }

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            await Leader.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<Fixture> CreateFixture(MockCluster cluster, INodeController nodeController)
    {
        SharpConfig config = new() { NodeName = NodeName, NodeStatusUpdateInterval = 1 };
        LeaderElectionService leader =
            new(config, cluster.Kubernetes, NullLogger<LeaderElectionService>.Instance);
        await leader.StartAsync(CancellationToken.None);
        NodeControllerService service =
            new(nodeController, cluster.Kubernetes, NullLogger<NodeControllerService>.Instance, config, leader);
        await service.StartAsync(CancellationToken.None);
        return new Fixture(cluster, nodeController, leader, service);
    }

    private static INodeController CreateNodeController(List<V1Node> createdNodes)
    {
        INodeController nodeController = Substitute.For<INodeController>();
        nodeController.GetNodeStatusAsync(NodeName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new V1NodeStatus
            {
                Conditions = new List<V1NodeCondition>
                {
                    new() { Type = "Ready", Status = "True" },
                },
            }));
        nodeController.CreateNodeAsync(Arg.Do<V1Node>(node => createdNodes.Add(node)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return nodeController;
    }

    [Fact]
    public async Task Leader_PatchesNodeStatusFromProvider()
    {
        MockCluster cluster = MockCluster.CreateLeader();
        List<V1Patch> patches = new();
        cluster.CoreV1.PatchNodeStatusWithHttpMessagesAsync(
                Arg.Do<V1Patch>(patch => patches.Add(patch)), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HttpOperationResponse<V1Node>()));
        cluster.CoreV1.ReadNodeWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HttpOperationResponse<V1Node>
            {
                Body = new V1Node { Metadata = new V1ObjectMeta { Name = NodeName } },
            }));

        INodeController nodeController = CreateNodeController(new());
        await using Fixture fixture = await CreateFixture(cluster, nodeController);

        await TestHelper.WaitUntilAsync(() => fixture.Leader.IsLeader);
        await TestHelper.WaitUntilAsync(() => patches.Count > 0);

        _ = cluster.CoreV1.Received().ReadNodeWithHttpMessagesAsync(NodeName, Arg.Any<bool?>(),
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());
        _ = nodeController.Received().GetNodeStatusAsync(NodeName, Arg.Any<CancellationToken>());
        // The merge patch wraps the node the API returned with the provider's status.
        V1Patch patch = patches[0];
        Assert.Equal(V1Patch.PatchType.MergePatch, patch.Type);
        V1Node patched = Assert.IsType<V1Node>(patch.Content);
        Assert.Equal(NodeName, patched.Metadata?.Name);
        V1NodeCondition ready = Assert.Single(patched.Status?.Conditions!);
        Assert.Equal("Ready", ready.Type);
        Assert.Equal("True", ready.Status);
    }

    [Fact]
    public async Task NodeNotFound_AskingProviderToRecreateIt()
    {
        MockCluster cluster = MockCluster.CreateLeader();
        cluster.CoreV1.ReadNodeWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<HttpOperationResponse<V1Node>>(
                TestHelper.ApiException(HttpStatusCode.NotFound, "node not found")));

        List<V1Node> createdNodes = new();
        INodeController nodeController = CreateNodeController(createdNodes);
        await using Fixture fixture = await CreateFixture(cluster, nodeController);

        await TestHelper.WaitUntilAsync(() => fixture.Leader.IsLeader);
        await TestHelper.WaitUntilAsync(() => createdNodes.Count > 0);
        // Stop the loop before the next 404 tick asks the provider to create the node again.
        await fixture.Service.StopAsync(CancellationToken.None);
        await fixture.Leader.StopAsync(CancellationToken.None);

        V1Node created = Assert.Single(createdNodes);
        Assert.Equal(NodeName, created.Metadata?.Name);
        _ = cluster.CoreV1.DidNotReceiveWithAnyArgs().PatchNodeStatusWithHttpMessagesAsync(
            Arg.Any<V1Patch>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecreationFailure_ServiceKeepsRunning()
    {
        MockCluster cluster = MockCluster.CreateLeader();
        cluster.CoreV1.ReadNodeWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<HttpOperationResponse<V1Node>>(
                TestHelper.ApiException(HttpStatusCode.NotFound, "node not found")));

        INodeController nodeController = Substitute.For<INodeController>();
        nodeController.CreateNodeAsync(Arg.Any<V1Node>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("provider is down")));
        RecordingLogger<NodeControllerService> logger = new();

        SharpConfig config = new() { NodeName = NodeName, NodeStatusUpdateInterval = 1 };
        LeaderElectionService leader =
            new(config, cluster.Kubernetes, NullLogger<LeaderElectionService>.Instance);
        await leader.StartAsync(CancellationToken.None);
        NodeControllerService service =
            new(nodeController, cluster.Kubernetes, logger, config, leader);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await TestHelper.WaitUntilAsync(() => leader.IsLeader);
            // The 404 surfaces a provider failure; the service logs it and backs off instead of stopping.
            await TestHelper.WaitUntilAsync(() => logger.Entries.Any(entry => entry.Level == LogLevel.Error));
            Assert.Contains(logger.Entries, entry => entry.Message.Contains(NodeName));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            await leader.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task GenericApiFailure_ServiceKeepsRunning()
    {
        MockCluster cluster = MockCluster.CreateLeader();
        cluster.CoreV1.ReadNodeWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<HttpOperationResponse<V1Node>>(new Exception("api server unreachable")));

        INodeController nodeController = CreateNodeController(new());
        RecordingLogger<NodeControllerService> logger = new();

        SharpConfig config = new() { NodeName = NodeName, NodeStatusUpdateInterval = 1 };
        LeaderElectionService leader =
            new(config, cluster.Kubernetes, NullLogger<LeaderElectionService>.Instance);
        await leader.StartAsync(CancellationToken.None);
        NodeControllerService service =
            new(nodeController, cluster.Kubernetes, logger, config, leader);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await TestHelper.WaitUntilAsync(() => leader.IsLeader);
            await TestHelper.WaitUntilAsync(() => logger.Entries.Any(entry => entry.Level == LogLevel.Error));
            Assert.Contains(logger.Entries, entry =>
                entry.Exception is not null && entry.Message.Contains("consecutive failures"));
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
        INodeController nodeController = CreateNodeController(new());
        await using Fixture fixture = await CreateFixture(cluster, nodeController);

        // Two full ticks (1s interval) while the peer holds the lease.
        await TestHelper.WaitUntilAsync(() => fixture.Leader.LeaderIdentity == "peer-1");
        await Task.Delay(2500, TestContext.Current.CancellationToken);

        _ = cluster.CoreV1.DidNotReceiveWithAnyArgs().ReadNodeWithHttpMessagesAsync(
            Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
            Arg.Any<CancellationToken>());
        _ = cluster.CoreV1.DidNotReceiveWithAnyArgs().PatchNodeStatusWithHttpMessagesAsync(
            Arg.Any<V1Patch>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
            Arg.Any<CancellationToken>());
        _ = nodeController.DidNotReceiveWithAnyArgs().GetNodeStatusAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.False(fixture.Leader.IsLeader);
    }
}