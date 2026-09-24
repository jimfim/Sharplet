using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Sharplet.Core.Tests;

/// <summary>
/// Exercises the provider-error reporter directly: terminal pods are left alone, the reported
/// phase follows the pod's restart policy, and the status patch and the Warning event are
/// independent best-effort writes (a failure of one does not skip the other).
/// </summary>
public class PodErrorReporterTests
{
    private static SharpConfig Config => new() { NodeName = "sharplet" };

    private static V1Pod CreatePod(string phase = "Pending", string restartPolicy = "Always")
    {
        return new V1Pod
        {
            ApiVersion = "v1",
            Kind = "Pod",
            Metadata = new V1ObjectMeta
            {
                Name = "test-pod",
                NamespaceProperty = "default",
                ResourceVersion = "1",
                Uid = "uid-test",
            },
            Spec = new V1PodSpec { NodeName = "sharplet", RestartPolicy = restartPolicy },
            Status = new V1PodStatus { Phase = phase },
        };
    }

    private sealed class ApiMocks
    {
        public ApiMocks(IKubernetes kubernetes, List<V1Patch> patches, List<Corev1Event> events)
        {
            Kubernetes = kubernetes;
            Patches = patches;
            Events = events;
        }

        public IKubernetes Kubernetes { get; }
        public List<V1Patch> Patches { get; }
        public List<Corev1Event> Events { get; }
    }

    private static ApiMocks MockApi(bool patchFails, bool eventFails,
        out RecordingLogger<PodErrorReporter> logger)
    {
        logger = new RecordingLogger<PodErrorReporter>();
        List<V1Patch> patches = new();
        List<Corev1Event> events = new();
        ICoreV1Operations coreV1 = Substitute.For<ICoreV1Operations>();
        coreV1.PatchNamespacedPodStatusWithHttpMessagesAsync(
                Arg.Do<V1Patch>(patch => patches.Add(patch)), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(patchFails
                ? Task.FromException<HttpOperationResponse<V1Pod>>(new Exception("patch api down"))
                : Task.FromResult(new HttpOperationResponse<V1Pod>()));
        coreV1.CreateNamespacedEventWithHttpMessagesAsync(
                Arg.Do<Corev1Event>(e => events.Add(e)), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(eventFails
                ? Task.FromException<HttpOperationResponse<Corev1Event>>(new Exception("event api down"))
                : Task.FromResult(new HttpOperationResponse<Corev1Event>()));
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        kubernetes.CoreV1.Returns(coreV1);
        return new ApiMocks(kubernetes, patches, events);
    }

    [Fact]
    public async Task TerminalPhase_PodLeftAlone()
    {
        // A late provider error must not flip a Succeeded or Failed pod back to Pending.
        ApiMocks api = MockApi(patchFails: false, eventFails: false, out _);
        PodErrorReporter reporter = new(Config, api.Kubernetes, new RecordingLogger<PodErrorReporter>());

        V1Pod pod = CreatePod(phase: "Succeeded");
        await reporter.ReportProviderErrorAsync(pod, new Exception("late failure"), "create", CancellationToken.None);

        Assert.Empty(api.Patches);
        Assert.Empty(api.Events);
        Assert.Equal("Succeeded", pod.Status.Phase);
    }

    [Theory]
    [InlineData("Always", "Pending")]
    [InlineData("Never", "Failed")]
    [InlineData("OnFailure", "Pending")]
    public async Task Phase_FollowsRestartPolicy(string restartPolicy, string expectedPhase)
    {
        ApiMocks api = MockApi(patchFails: false, eventFails: false, out _);
        PodErrorReporter reporter = new(Config, api.Kubernetes, new RecordingLogger<PodErrorReporter>());

        await reporter.ReportProviderErrorAsync(CreatePod(restartPolicy: restartPolicy),
            new Exception("backend exploded"), "create", CancellationToken.None);

        V1Pod patched = (V1Pod)Assert.Single(api.Patches).Content;
        Assert.Equal(expectedPhase, patched.Status.Phase);
        Assert.Equal("ProviderFailed", patched.Status.Reason);
        Assert.Equal("backend exploded", patched.Status.Message);
        Assert.Equal(string.Empty, patched.ResourceVersion());
    }

    [Theory]
    [InlineData("create", "ProviderCreateFailed")]
    [InlineData("update", "ProviderUpdateFailed")]
    public async Task WarningEvent_ReasonFollowsOperation(string operation, string expectedReason)
    {
        ApiMocks api = MockApi(patchFails: false, eventFails: false, out _);
        PodErrorReporter reporter = new(Config, api.Kubernetes, new RecordingLogger<PodErrorReporter>());

        await reporter.ReportProviderErrorAsync(CreatePod(), new Exception("backend exploded"), operation, CancellationToken.None);

        Corev1Event warning = Assert.Single(api.Events);
        Assert.Equal("Warning", warning.Type);
        Assert.Equal(expectedReason, warning.Reason);
        Assert.Equal("sharplet", warning.ReportingComponent);
        Assert.Equal("test-pod", warning.InvolvedObject.Name);
        Assert.Equal("default", warning.InvolvedObject.NamespaceProperty);
    }

    [Fact]
    public async Task StatusPatchFailure_EventStillRecorded()
    {
        ApiMocks api = MockApi(patchFails: true, eventFails: false, out RecordingLogger<PodErrorReporter> logger);
        PodErrorReporter reporter = new(Config, api.Kubernetes, logger);

        await reporter.ReportProviderErrorAsync(CreatePod(), new Exception("backend exploded"), "create", CancellationToken.None);

        Assert.Single(api.Events);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task EventFailure_StatusStillPatched()
    {
        ApiMocks api = MockApi(patchFails: false, eventFails: true, out RecordingLogger<PodErrorReporter> logger);
        PodErrorReporter reporter = new(Config, api.Kubernetes, logger);

        await reporter.ReportProviderErrorAsync(CreatePod(), new Exception("backend exploded"), "create", CancellationToken.None);

        Assert.Single(api.Patches);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);
    }
}