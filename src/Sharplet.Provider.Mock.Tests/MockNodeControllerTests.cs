using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Sharplet.Provider.Mock.Tests;

/// <summary>
/// Behaviour of the reference node provider: the node object it registers with the API
/// server, how it survives a node that outlived the process, and how it reports its status.
/// Runs serialized with the other test classes in this project because it rewrites the
/// <c>VKUBELET_POD_IP</c>/<c>POD_IP</c> environment variables, which its sibling also reads.
/// </summary>
[Collection("environment")]
public class MockNodeControllerTests
{
    [Fact]
    public async Task CreateNodeAsync_RegistersVirtualKubeletNode()
    {
        using EnvVariables env = EnvVariables.Scope();
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        V1Node? created = null;
        kubernetes.CoreV1.CreateNodeWithHttpMessagesAsync(
                Arg.Do<V1Node>(node => created = node), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new HttpOperationResponse<V1Node> { Body = ci.ArgAt<V1Node>(0) }));
        MockNodeController controller = new(NullLogger<MockNodeController>.Instance, kubernetes);

        await controller.CreateNodeAsync(new V1Node(), CancellationToken.None);

        Assert.NotNull(created);
        // The node must be recognizably a virtual kubelet node: labelled for the service
        // controller and load balancers, tained so real workloads never schedule onto it,
        // and immortal (ttl 0) so it survives kubelet restarts.
        Assert.Equal("virtual-kubelet", created!.Metadata.Labels["type"]);
        Assert.Equal("agent", created.Metadata.Labels["kubernetes.io/role"]);
        Assert.Equal("0", created.Metadata.Annotations["node.alpha.kubernetes.io/ttl"]);
        Assert.Equal("true", created.Metadata.Annotations["volumes.kubernetes.io/controller-managed-attach-detach"]);
        Assert.Equal(2, created.Spec.Taints.Count);
        Assert.All(created.Spec.Taints, taint => Assert.Equal("kubernetes.io/sharplet", taint.Key));
        Assert.Contains(created.Spec.Taints, taint => taint.Effect == "NoSchedule");
        Assert.Contains(created.Spec.Taints, taint => taint.Effect == "NoExecute");

        // With no VKUBELET_POD_IP/POD_IP the node advertises loopback: the only address the
        // provider can honestly report outside a pod.
        V1NodeAddress internalIp = created.Status.Addresses.First(address => address.Type == "InternalIP");
        Assert.Equal("127.0.0.1", internalIp.Address);
        Assert.NotNull(created.Status.Addresses.First(address => address.Type == "Hostname"));

        Assert.Equal("10", created.Status.Allocatable["cpu"].ToString());
        Assert.Equal("5", created.Status.Capacity["pods"].ToString());
        V1NodeCondition ready = created.Status.Conditions.First(condition => condition.Type == "Ready");
        Assert.Equal("True", ready.Status);
        Assert.Equal("False", created.Status.Conditions.First(condition => condition.Type == "MemoryPressure").Status);
        Assert.Equal(10250, created.Status.DaemonEndpoints.KubeletEndpoint.Port);
    }

    [Fact]
    public async Task CreateNodeAsync_WhenNodeAlreadyExists_KeepsTheExistingObject()
    {
        // The node object outlives the process, so every restart hits a 409 Conflict on
        // create. The provider must swallow it instead of crashing the status loop.
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        kubernetes.CoreV1.CreateNodeWithHttpMessagesAsync(
                Arg.Any<V1Node>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<HttpOperationResponse<V1Node>>(HttpError(HttpStatusCode.Conflict)));
        MockNodeController controller = new(NullLogger<MockNodeController>.Instance, kubernetes);

        await controller.CreateNodeAsync(new V1Node(), CancellationToken.None);

        await kubernetes.CoreV1.Received(1).CreateNodeWithHttpMessagesAsync(
            Arg.Any<V1Node>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateNodeAsync_WhenCreateFails_Throws()
    {
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        kubernetes.CoreV1.CreateNodeWithHttpMessagesAsync(
                Arg.Any<V1Node>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<HttpOperationResponse<V1Node>>(HttpError(HttpStatusCode.InternalServerError)));
        MockNodeController controller = new(NullLogger<MockNodeController>.Instance, kubernetes);

        await Assert.ThrowsAsync<HttpOperationException>(
            () => controller.CreateNodeAsync(new V1Node(), CancellationToken.None));
    }

    [Fact]
    public async Task GetNodeAsync_ReadsTheNodeFromTheApiServer()
    {
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        V1Node node = new();
        kubernetes.CoreV1.ReadNodeWithHttpMessagesAsync(
                "test-node", Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                CancellationToken.None)
            .Returns(Task.FromResult(new HttpOperationResponse<V1Node> { Body = node }));
        MockNodeController controller = new(NullLogger<MockNodeController>.Instance, kubernetes);

        V1Node? read = await controller.GetNodeAsync("test-node", CancellationToken.None);

        Assert.Same(node, read);
        await kubernetes.CoreV1.Received(1).ReadNodeWithHttpMessagesAsync(
            "test-node", Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
            CancellationToken.None);
    }

    [Fact]
    public async Task DeleteNodeAsync_DeletesTheNodeFromTheApiServer()
    {
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        kubernetes.CoreV1.DeleteNodeWithHttpMessagesAsync(
                "test-node", Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<bool?>(),
                Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), CancellationToken.None)
            .Returns(Task.FromResult(new HttpOperationResponse<V1Status>()));
        MockNodeController controller = new(NullLogger<MockNodeController>.Instance, kubernetes);

        await controller.DeleteNodeAsync("test-node", CancellationToken.None);

        await kubernetes.CoreV1.Received(1).DeleteNodeWithHttpMessagesAsync(
            "test-node", Arg.Any<V1DeleteOptions>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<bool?>(),
            Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), CancellationToken.None);
    }

    [Fact]
    public async Task GetNodeStatusAsync_WithoutPodIp_ReportsLoopbackOnce()
    {
        // The loopback fallback is fine for the mock but unreachable from the cluster, so
        // the provider warns; the warning fires once per process, not on every status tick.
        using EnvVariables env = EnvVariables.Scope();
        CapturingLogger<MockNodeController> logger = new();
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        MockNodeController controller = new(logger, kubernetes);

        V1NodeStatus first = await controller.GetNodeStatusAsync("test-node", CancellationToken.None);
        V1NodeStatus second = await controller.GetNodeStatusAsync("test-node", CancellationToken.None);

        Assert.Equal("127.0.0.1", first.Addresses.First(address => address.Type == "InternalIP").Address);
        Assert.Equal("test-node", second.Addresses.First(address => address.Type == "Hostname").Address);
        Assert.Equal("True", first.Conditions.First(condition => condition.Type == "Ready").Status);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task GetNodeStatusAsync_WithPodIp_ReportsThePodIp()
    {
        using EnvVariables env = EnvVariables.Scope(kubeletIp: "10.0.0.5");
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        MockNodeController controller = new(NullLogger<MockNodeController>.Instance, kubernetes);

        V1NodeStatus status = await controller.GetNodeStatusAsync("test-node", CancellationToken.None);

        Assert.Equal("10.0.0.5", status.Addresses.First(address => address.Type == "InternalIP").Address);
    }

    private static HttpOperationException HttpError(HttpStatusCode statusCode)
    {
        HttpOperationException error = new("api error");
        error.Response = new HttpResponseMessageWrapper(new HttpResponseMessage(statusCode), string.Empty);
        return error;
    }

    /// <summary>Captures every log entry written to it so tests can assert on what the provider logged.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public sealed record Entry(LogLevel Level, string Message);

        public List<Entry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new Entry(logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}