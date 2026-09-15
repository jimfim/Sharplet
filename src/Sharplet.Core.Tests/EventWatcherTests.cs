using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Sharplet.Core.Tests;

public class EventWatcherTests
{
    private readonly List<Corev1Event> _emittedEvents = new();
    private readonly List<V1Pod> _createdPods = new();
    private readonly List<V1Pod> _updatedPods = new();
    private readonly List<V1Pod> _deletedPods = new();
    private readonly IPodController _podController;
    private readonly EventWatcher _watcher;

    public EventWatcherTests()
    {
        ICoreV1Operations coreV1 = Substitute.For<ICoreV1Operations>();
        coreV1.CreateNamespacedEventWithHttpMessagesAsync(
            Arg.Do<Corev1Event>(e => _emittedEvents.Add(e)), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(new HttpOperationResponse<Corev1Event>());
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        kubernetes.CoreV1.Returns(coreV1);

        _podController = Substitute.For<IPodController>();
        _podController.CreatePodAsync(Arg.Do<V1Pod>(p => _createdPods.Add(p)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _podController.UpdatePodAsync(Arg.Do<V1Pod>(p => _updatedPods.Add(p)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _podController.DeletePodAsync(Arg.Do<V1Pod>(p => _deletedPods.Add(p)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _watcher = new EventWatcher(NullLogger<EventWatcher>.Instance, kubernetes, _podController,
            new SharpConfig { NodeName = "sharplet" });
    }

    private static V1Pod CreatePod(string image = "nginx:1.27", string phase = "Running",
        string resourceVersion = "1")
    {
        return new V1Pod
        {
            ApiVersion = "v1",
            Kind = "Pod",
            Metadata = new V1ObjectMeta
            {
                Name = "test-pod",
                NamespaceProperty = "default",
                ResourceVersion = resourceVersion,
                Uid = "uid-test",
            },
            Spec = new V1PodSpec
            {
                NodeName = "sharplet",
                Containers = new List<V1Container> { new() { Name = "app", Image = image } },
            },
            Status = new V1PodStatus { Phase = phase },
        };
    }

    [Fact]
    public async Task Added_CreatesPodAndEmitsStartedEvent()
    {
        V1Pod pod = CreatePod();
        await _watcher.HandlePodEventAsync(WatchEventType.Added, pod);

        Assert.Same(pod, Assert.Single(_createdPods));
        Assert.Empty(_updatedPods);
        Corev1Event started = Assert.Single(_emittedEvents);
        Assert.Equal("Started", started.Reason);
        Assert.Equal("sharplet", started.ReportingComponent);
        Assert.Equal("default", started.InvolvedObject.NamespaceProperty);
    }

    [Fact]
    public async Task Modified_StatusOnlyChange_Ignored()
    {
        await _watcher.HandlePodEventAsync(WatchEventType.Added, CreatePod());

        // Same spec, new status and resourceVersion: the churn PodControllerService produces
        // with its periodic status patches.
        await _watcher.HandlePodEventAsync(WatchEventType.Modified,
            CreatePod(phase: "Succeeded", resourceVersion: "42"));

        Assert.Empty(_updatedPods);
        Assert.Empty(_deletedPods);
        Corev1Event only = Assert.Single(_emittedEvents);
        Assert.Equal("Started", only.Reason);
    }

    [Fact]
    public async Task Modified_SpecChange_UpdatesPodAndEmitsPatchedEvent()
    {
        await _watcher.HandlePodEventAsync(WatchEventType.Added, CreatePod());

        await _watcher.HandlePodEventAsync(WatchEventType.Modified,
            CreatePod(image: "nginx:1.28", resourceVersion: "7"));

        Assert.Single(_createdPods);
        Assert.Single(_updatedPods);
        Assert.Equal(2, _emittedEvents.Count);
        Assert.Equal("Started", _emittedEvents[0].Reason);
        Assert.Equal("Patched", _emittedEvents[1].Reason);
        Assert.Equal("Normal", _emittedEvents[1].Type);
    }

    [Fact]
    public async Task Modified_FirstSightOnNode_CreatesPodAndEmitsStartedEvent()
    {
        // The pod is already scheduled onto this node when it first reaches the watcher
        // (e.g. the scheduler set spec.nodeName after the pod was created).
        await _watcher.HandlePodEventAsync(WatchEventType.Modified, CreatePod(resourceVersion: "3"));

        Assert.Single(_createdPods);
        Assert.Empty(_updatedPods);
        Corev1Event started = Assert.Single(_emittedEvents);
        Assert.Equal("Started", started.Reason);
    }

    [Fact]
    public async Task Added_DuplicatePod_EmitsNoSecondStart()
    {
        // A watch reconnect re-lists every pod: pods that already started on this node
        // must not be created or started again.
        await _watcher.HandlePodEventAsync(WatchEventType.Added, CreatePod());
        await _watcher.HandlePodEventAsync(WatchEventType.Added, CreatePod(resourceVersion: "9"));

        Assert.Single(_createdPods);
        Assert.Single(_emittedEvents);
    }

    [Fact]
    public async Task Deleted_RemovesPodAndClearsTrackedState()
    {
        await _watcher.HandlePodEventAsync(WatchEventType.Added, CreatePod());
        await _watcher.HandlePodEventAsync(WatchEventType.Deleted, CreatePod(resourceVersion: "10"));

        Assert.Single(_deletedPods);

        // The pod is recreated with the same name: the tracked state was cleared, so it
        // starts again instead of being swallowed as a duplicate.
        await _watcher.HandlePodEventAsync(WatchEventType.Added, CreatePod(resourceVersion: "11"));
        Assert.Equal(2, _createdPods.Count);
        Assert.Equal(2, _emittedEvents.Count);
    }

    [Fact]
    public async Task Modified_PodOnOtherNode_Ignored()
    {
        V1Pod pod = CreatePod();
        pod.Spec.NodeName = "other-node";
        await _watcher.HandlePodEventAsync(WatchEventType.Modified, pod);

        Assert.Empty(_createdPods);
        Assert.Empty(_updatedPods);
        Assert.Empty(_deletedPods);
        Assert.Empty(_emittedEvents);
    }

    [Fact]
    public async Task Error_Event_TakesNoAction()
    {
        await _watcher.HandlePodEventAsync(WatchEventType.Error, CreatePod());

        Assert.Empty(_createdPods);
        Assert.Empty(_updatedPods);
        Assert.Empty(_deletedPods);
        Assert.Empty(_emittedEvents);
    }

    [Fact]
    public async Task Bookmark_Event_TakesNoAction()
    {
        await _watcher.HandlePodEventAsync(WatchEventType.Bookmark, CreatePod());

        Assert.Empty(_createdPods);
        Assert.Empty(_updatedPods);
        Assert.Empty(_deletedPods);
        Assert.Empty(_emittedEvents);
    }

    [Fact]
    public async Task WatchEventStream_StartsWatchingAllNamespaces()
    {
        // The full watch pipeline cannot be driven from a mocked IKubernetes: the client's
        // watcher requires its internal LineSeparatedHttpContent on the response, which a
        // substitute cannot produce (it logs "not a watchable request" instead). So this
        // covers what is observable: WatchEventStream starts the all-namespaces pod watch
        // with watch=true; event handling from that stream is covered by the
        // HandlePodEventAsync tests.
        ICoreV1Operations coreV1 = Substitute.For<ICoreV1Operations>();
        coreV1.CreateNamespacedEventWithHttpMessagesAsync(
            Arg.Any<Corev1Event>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new HttpOperationResponse<Corev1Event>());
        bool? watchFlag = null;
        coreV1.ListPodForAllNamespacesWithHttpMessagesAsync(
                Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(),
                Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<int?>(),
                Arg.Do<bool?>(watch => watchFlag = watch),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HttpOperationResponse<V1PodList>()));
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        kubernetes.CoreV1.Returns(coreV1);

        IPodController podController = Substitute.For<IPodController>();
        EventWatcher watcher = new EventWatcher(NullLogger<EventWatcher>.Instance, kubernetes, podController,
            new SharpConfig { NodeName = "sharplet" });

        await watcher.WatchEventStream();

        await TestHelper.WaitUntilAsync(() => watchFlag is true);
        await coreV1.Received(1).ListPodForAllNamespacesWithHttpMessagesAsync(
            Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(),
            Arg.Any<bool?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<int?>(),
            Arg.Is<bool?>(watch => watch == true),
            Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EventPublishFailure_PropagatesFromPodEventHandling()
    {
        // A failed event publish surfaces out of the handler. The watch stream dispatches
        // handlers fire-and-forget, so today that faults an unobserved task: kept explicit
        // so a future hardening (catch + log inside the dispatch) has to update this test.
        ICoreV1Operations coreV1 = Substitute.For<ICoreV1Operations>();
        coreV1.CreateNamespacedEventWithHttpMessagesAsync(
            Arg.Any<Corev1Event>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromException<HttpOperationResponse<Corev1Event>>(new Exception("events api down")));
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        kubernetes.CoreV1.Returns(coreV1);

        IPodController podController = Substitute.For<IPodController>();
        podController.CreatePodAsync(Arg.Any<V1Pod>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        EventWatcher watcher = new EventWatcher(NullLogger<EventWatcher>.Instance, kubernetes, podController,
            new SharpConfig { NodeName = "sharplet" });

        await Assert.ThrowsAnyAsync<Exception>(() => watcher.HandlePodEventAsync(WatchEventType.Added, CreatePod()));
    }
}