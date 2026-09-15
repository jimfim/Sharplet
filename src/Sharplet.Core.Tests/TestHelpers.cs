using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Sharplet.Core.Tests;

/// <summary>
/// Shared building blocks for the service tests: polling waits, a logging logger and mocked
/// Kubernetes API surfaces for the leader-election scenarios the status services run against.
/// </summary>
internal static class TestHelper
{
    /// <summary>Polls <paramref name="condition"/> every 50ms until it is true or the deadline passes.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutSeconds = 10)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"condition was not met within {timeoutSeconds}s");
            }
            await Task.Delay(50);
        }
    }

    public static HttpOperationException ApiException(HttpStatusCode statusCode, string message = "api error")
    {
        HttpResponseMessageWrapper response = new(new HttpResponseMessage(statusCode), null);
        return new HttpOperationException(message) { Response = response };
    }
}

/// <summary>
/// Captures every log entry written to it so tests can assert on what a service logged.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

    public List<Entry> Entries { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Points <c>KUBECONFIG</c> at a minimal on-disk kubeconfig so code paths that resolve the
/// in-cluster-or-file client configuration (e.g. <c>AddVirtualKubelet</c>) work outside a cluster.
/// </summary>
internal sealed class TemporaryKubeConfig : IDisposable
{
    private readonly string? _previousKubeConfig;
    public string Path { get; }

    public TemporaryKubeConfig()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sharplet-test-kubeconfig-{Guid.NewGuid():N}.yaml");
        System.IO.File.WriteAllText(Path,
            """
            apiVersion: v1
            kind: Config
            clusters:
            - cluster:
                server: https://127.0.0.1:6443
              name: test-cluster
            contexts:
            - context:
                cluster: test-cluster
                user: test-user
              name: test-context
            current-context: test-context
            users:
            - name: test-user
              user:
                token: test-token
            """);
        _previousKubeConfig = Environment.GetEnvironmentVariable("KUBECONFIG");
        Environment.SetEnvironmentVariable("KUBECONFIG", Path);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("KUBECONFIG", _previousKubeConfig);
        System.IO.File.Delete(Path);
    }
}

/// <summary>
/// A mocked Kubernetes API surface for the lease-based leader election the status services
/// gate on. <see cref="CreateLeader"/> hands the elector a missing lease (so it creates the
/// record and wins on its first attempt); <see cref="CreateFollower"/> hands it a fresh
/// lease held by a peer, which the elector must not preempt.
/// </summary>
internal sealed class MockCluster
{
    private MockCluster(IKubernetes kubernetes, ICoreV1Operations coreV1, ICoordinationV1Operations coordinationV1)
    {
        Kubernetes = kubernetes;
        CoreV1 = coreV1;
        CoordinationV1 = coordinationV1;
    }

    public IKubernetes Kubernetes { get; }
    public ICoreV1Operations CoreV1 { get; }
    public ICoordinationV1Operations CoordinationV1 { get; }

    public static MockCluster CreateLeader()
    {
        ICoreV1Operations coreV1 = Substitute.For<ICoreV1Operations>();
        // The namespace already exists in a real cluster: the service tolerates the conflict.
        coreV1.CreateNamespaceWithHttpMessagesAsync(
                Arg.Any<V1Namespace>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<HttpOperationResponse<V1Namespace>>(
                TestHelper.ApiException(HttpStatusCode.Conflict, "namespace already exists")));
        ICoordinationV1Operations coordinationV1 = Substitute.For<ICoordinationV1Operations>();
        coordinationV1.ReadNamespacedLeaseWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<HttpOperationResponse<V1Lease>>(
                TestHelper.ApiException(HttpStatusCode.NotFound, "lease not found")));
        coordinationV1.CreateNamespacedLeaseWithHttpMessagesAsync(
                Arg.Any<V1Lease>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new HttpOperationResponse<V1Lease> { Body = ci.ArgAt<V1Lease>(0) }));
        coordinationV1.ReplaceNamespacedLeaseWithHttpMessagesAsync(
                Arg.Any<V1Lease>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new HttpOperationResponse<V1Lease> { Body = ci.ArgAt<V1Lease>(0) }));
        return Create(coreV1, coordinationV1);
    }

    public static MockCluster CreateFollower(string peerIdentity)
    {
        ICoreV1Operations coreV1 = Substitute.For<ICoreV1Operations>();
        coreV1.CreateNamespaceWithHttpMessagesAsync(
                Arg.Any<V1Namespace>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HttpOperationResponse<V1Namespace>()));
        ICoordinationV1Operations coordinationV1 = Substitute.For<ICoordinationV1Operations>();
        DateTime now = DateTime.UtcNow;
        V1Lease peerLease = new()
        {
            Metadata = new V1ObjectMeta { Name = "sharplet", NamespaceProperty = LeaderElectionService.LeaseNamespace },
            Spec = new V1LeaseSpec
            {
                AcquireTime = now.AddSeconds(-1),
                RenewTime = now.AddSeconds(-1),
                HolderIdentity = peerIdentity,
                LeaseDurationSeconds = 40,
            },
        };
        coordinationV1.ReadNamespacedLeaseWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool?>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HttpOperationResponse<V1Lease> { Body = peerLease }));
        return Create(coreV1, coordinationV1);
    }

    private static MockCluster Create(ICoreV1Operations coreV1, ICoordinationV1Operations coordinationV1)
    {
        IKubernetes kubernetes = Substitute.For<IKubernetes>();
        kubernetes.CoreV1.Returns(coreV1);
        kubernetes.CoordinationV1.Returns(coordinationV1);
        return new MockCluster(kubernetes, coreV1, coordinationV1);
    }
}