using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Sharplet.Core.Tests;

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

        MockPodController controller = new(NullLogger<MockPodController>.Instance, kubernetes);
        V1Pod pod = new()
        {
            Metadata = new V1ObjectMeta { Name = "test-pod", NamespaceProperty = "default" },
        };

        await controller.DeletePodAsync(pod, CancellationToken.None);

        // The provider must never delete the API object; that is the user's or controller's job.
        await coreV1.DidNotReceiveWithAnyArgs().DeleteNamespacedPodWithHttpMessagesAsync(
            "default", "test-pod", new V1DeleteOptions(), "pretty", null, null, null, "dryRun", null,
            new Dictionary<string, IReadOnlyList<string>>(), CancellationToken.None);
    }
}