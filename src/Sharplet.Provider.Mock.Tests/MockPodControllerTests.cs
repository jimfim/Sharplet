using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Sharplet.Provider.Mock.Tests;

[Collection("environment")]
public class MockPodControllerTests
{
    [Fact]
    public async Task DeletePodAsync_DoesNotDeletePodFromApiServer()
    {
        // A delete for a pod the user/controller already removed is answered by the API server with a
        // 404. Re-deleting the API object from a provider surfaced that 404 in the watch loop and
        // crashed the kubelet (issue #6); a provider only releases its local state.
        ICoreV1Operations coreV1 = Substitute.For<ICoreV1Operations>();
        coreV1.DeleteNamespacedPodWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<V1DeleteOptions>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<HttpOperationResponse<V1Pod>>(new HttpOperationException(
                "Not Found: {\"message\":\"pods \\\"test-pod\\\" not found\"}")));
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        kubernetes.CoreV1.Returns(coreV1);

        CapturingLogger<MockPodController> logger = new();
        MockPodController controller = new(logger, kubernetes);
        V1Pod pod = new()
        {
            Metadata = new V1ObjectMeta { Name = "test-pod", NamespaceProperty = "default" },
        };

        await controller.DeletePodAsync(pod, CancellationToken.None);

        // The provider must never delete the API object; that is the user's or controller's job.
        await coreV1.DidNotReceiveWithAnyArgs().DeleteNamespacedPodWithHttpMessagesAsync(
            "default", "test-pod", new V1DeleteOptions(), "pretty", null, null, null, "dryRun", null,
            new Dictionary<string, IReadOnlyList<string>>(), CancellationToken.None);
        // The provider only releases local state; the skip is recorded for operators.
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information
            && entry.Message.Contains("DeletePodAsync"));
    }

    [Fact]
    public async Task CreatePodAsync_DoesNotTouchTheApiServer()
    {
        ICoreV1Operations coreV1 = Substitute.For<ICoreV1Operations>();
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        kubernetes.CoreV1.Returns(coreV1);
        MockPodController controller = new(NullLogger<MockPodController>.Instance, kubernetes);

        await controller.CreatePodAsync(new V1Pod(), CancellationToken.None);

        // ListNamespacedPodAsync is an extension over the WithHttpMessages interface member, so that is
        // what the stub must target.
        await coreV1.DidNotReceiveWithAnyArgs().ListNamespacedPodWithHttpMessagesAsync(
            Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<int?>(),
            Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdatePodAsync_ListsThePodsInThePodNamespace()
    {
        ICoreV1Operations coreV1 = Substitute.For<ICoreV1Operations>();
        StubListPods(coreV1, new V1PodList());
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        kubernetes.CoreV1.Returns(coreV1);
        MockPodController controller = new(NullLogger<MockPodController>.Instance, kubernetes);
        V1Pod pod = new()
        {
            Metadata = new V1ObjectMeta { Name = "test-pod", NamespaceProperty = "default" },
        };

        await controller.UpdatePodAsync(pod, CancellationToken.None);

        VerifyListedPods(coreV1, "default");
    }

    [Fact]
    public async Task GetPodAsync_ReturnsThePodWithTheMatchingName()
    {
        ICoreV1Operations coreV1 = Substitute.For<ICoreV1Operations>();
        V1Pod match = new()
        {
            Metadata = new V1ObjectMeta { Name = "test-pod", NamespaceProperty = "default" },
        };
        StubListPods(coreV1, new V1PodList
        {
            Items = new List<V1Pod>
            {
                new() { Metadata = new V1ObjectMeta { Name = "other-pod" } },
                match,
            },
        });
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        kubernetes.CoreV1.Returns(coreV1);
        MockPodController controller = new(NullLogger<MockPodController>.Instance, kubernetes);

        V1Pod? found = await controller.GetPodAsync("default", "test-pod", CancellationToken.None);

        Assert.Same(match, found);
    }

    [Fact]
    public async Task GetPodAsync_WithoutAMatchingPod_ReturnsNull()
    {
        ICoreV1Operations coreV1 = Substitute.For<ICoreV1Operations>();
        StubListPods(coreV1, new V1PodList
        {
            Items = new List<V1Pod> { new() { Metadata = new V1ObjectMeta { Name = "other-pod" } } },
        });
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        kubernetes.CoreV1.Returns(coreV1);
        MockPodController controller = new(NullLogger<MockPodController>.Instance, kubernetes);

        V1Pod? found = await controller.GetPodAsync("default", "test-pod", CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task GetPodStatusAsync_ReportsARunningPodOnTheKubeletHost()
    {
        using EnvVariables env = EnvVariables.Scope(kubeletIp: "10.0.0.9");
        ICoreV1Operations coreV1 = Substitute.For<ICoreV1Operations>();
        HttpOperationResponse<V1Pod> response = new();
        response.Body = new V1Pod
        {
            Spec = new V1PodSpec
            {
                Containers = new List<V1Container> { new V1Container { Image = "busybox", Name = "app" } },
            },
        };
        coreV1.ReadNamespacedPodWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<string>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        kubernetes.CoreV1.Returns(coreV1);
        CapturingLogger<MockPodController> logger = new();
        MockPodController controller = new(logger, kubernetes);

        V1PodStatus status = await controller.GetPodStatusAsync("default", "test-pod", CancellationToken.None);

        // The mock claims every container is up and every gate condition is true; the IPs
        // it reports come from the pod's own address, so the API server can proxy to it.
        Assert.Equal("Running", status.Phase);
        V1ContainerStatus container = Assert.Single(status.ContainerStatuses);
        Assert.True(container.Ready);
        Assert.Equal("app", container.Name);
        Assert.Equal("busybox", container.Image);
        // A container that started once and never restarted reports restartCount 0.
        Assert.Equal(0, container.RestartCount);
        Assert.Equal("10.0.0.9", status.PodIP);
        Assert.Equal("10.0.0.9", status.HostIP);
        // The IP the mock reports is surfaced at debug level for local debugging.
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug
            && entry.Message.Contains("10.0.0.9"));
    }

    [Fact]
    public async Task GetPodsAsync_ReturnsThePodsInTheDefaultNamespace()
    {
        ICoreV1Operations coreV1 = Substitute.For<ICoreV1Operations>();
        StubListPods(coreV1, new V1PodList
        {
            Items = new List<V1Pod> { new() { Metadata = new V1ObjectMeta { Name = "test-pod" } } },
        });
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        kubernetes.CoreV1.Returns(coreV1);
        MockPodController controller = new(NullLogger<MockPodController>.Instance, kubernetes);

        IEnumerable<V1Pod> found = await controller.GetPodsAsync(CancellationToken.None);

        Assert.Single(found);
        VerifyListedPods(coreV1, "default");
    }

    [Fact]
    public async Task GetContainerLogs_YieldsTimestampedMessages()
    {
        MockPodController controller = new(NullLogger<MockPodController>.Instance, Substitute.For<IKubernetes>());
        IAsyncEnumerable<string> logs = await controller.GetContainerLogs("default", "test-pod", "app", TestContext.Current.CancellationToken);
        await using IAsyncEnumerator<string> enumerator = logs.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        string first = enumerator.Current;

        Assert.Contains("default test-pod app", first);
        Assert.Contains("Log message from Provider 0", first);
    }
    private static void StubListPods(ICoreV1Operations coreV1, V1PodList pods)
    {
        coreV1.ListNamespacedPodWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HttpOperationResponse<V1PodList> { Body = pods }));
    }

    private static void VerifyListedPods(ICoreV1Operations coreV1, string @namespace)
    {
        coreV1.Received(1).ListNamespacedPodWithHttpMessagesAsync(
            @namespace, Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<int?>(),
            Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
            CancellationToken.None);
    }
}