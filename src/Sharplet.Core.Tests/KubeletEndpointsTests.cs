using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using k8s.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Sharplet.Core.Tests;

/// <summary>
/// Contract tests for the kubelet HTTP surface mapped by <c>MapKubeletEndpoints</c>: the API
/// server proxies node-scoped requests (<c>kubectl get pods</c> on the virtual node,
/// <c>/stats/sum</c>) to these endpoints, so the JSON shapes here are load-bearing.
/// </summary>
public class KubeletEndpointsTests : IAsyncLifetime
{
    private readonly IPodController _podController = Substitute.For<IPodController>();
    private readonly List<V1Pod> _pods = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _pods.Add(CreatePod("web", "default", "Running"));
        _pods.Add(CreatePod("batch", "default", "Succeeded"));
        _podController.GetPodsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<V1Pod>>(_pods));

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(_podController);
        builder.Services.AddSingleton(new SharpConfig { NodeName = "test-node" });
        _app = builder.Build();
        _app.MapKubeletEndpoints();
        await _app.StartAsync();

        // Port 0: Kestrel picks a free port; read back the actual bound address.
        IServerAddressesFeature addresses = _app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!;
        _client = new HttpClient { BaseAddress = new Uri(addresses.Addresses.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static V1Pod CreatePod(string name, string @namespace, string phase)
    {
        return new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = @namespace },
            Spec = new V1PodSpec
            {
                Containers = new List<V1Container> { new() { Name = name + "-container", Image = "nginx:1.27" } }
            },
            Status = new V1PodStatus { Phase = phase },
        };
    }

    [Fact]
    public async Task Pods_ReturnsEveryPodTheProviderReports()
    {
        HttpResponseMessage response = await _client.GetAsync("/pods", TestContext.Current.CancellationToken);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        JsonElement array = document.RootElement;
        Assert.Equal(JsonValueKind.Array, array.ValueKind);
        Assert.Equal(2, array.GetArrayLength());
        Assert.Equal("web", array[0].GetProperty("metadata").GetProperty("name").GetString());
        Assert.Equal("default", array[0].GetProperty("metadata").GetProperty("namespace").GetString());
        Assert.Equal("batch", array[1].GetProperty("metadata").GetProperty("name").GetString());
    }

    [Fact]
    public async Task RunningPods_FiltersToRunningPhase()
    {
        HttpResponseMessage response = await _client.GetAsync("/runningpods", TestContext.Current.CancellationToken);
        Assert.Equal(200, (int)response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        JsonElement array = document.RootElement;
        Assert.Equal(1, array.GetArrayLength());
        Assert.Equal("web", array[0].GetProperty("metadata").GetProperty("name").GetString());
    }

    [Fact]
    public async Task StatsSum_ReportsNodeAndKnownPodsWithZeroUsage()
    {
        HttpResponseMessage response = await _client.GetAsync("/stats/sum", TestContext.Current.CancellationToken);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        JsonElement root = document.RootElement;
        Assert.Equal("stats.k8s.io/v1alpha1", root.GetProperty("apiVersion").GetString());
        Assert.Equal("StatsSummary", root.GetProperty("kind").GetString());
        Assert.Equal("test-node", root.GetProperty("node").GetProperty("node").GetString());

        JsonElement pods = root.GetProperty("pods");
        Assert.Equal(2, pods.GetArrayLength());
        Assert.Equal("web", pods[0].GetProperty("podRef").GetProperty("reference").GetProperty("name").GetString());
        Assert.Equal(0, pods[0].GetProperty("cpu").GetProperty("usageNanoCores").GetDouble());
        Assert.Equal("web-container", pods[0].GetProperty("containers")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Pods_ReportsEmptyArrayWhenNodeHasNoPods()
    {
        _podController.GetPodsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<V1Pod>>(Enumerable.Empty<V1Pod>()));

        HttpResponseMessage response = await _client.GetAsync("/pods", TestContext.Current.CancellationToken);
        Assert.Equal(200, (int)response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, document.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Livez_ReturnsOk()
    {
        HttpResponseMessage response = await _client.GetAsync("/livez", TestContext.Current.CancellationToken);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("\"alive\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Probes_ReportNotReady_BeforeElectionSynchronizes()
    {
        // The test host registers no LeaderElectionService: a replica that neither holds the
        // node lease nor has observed a leader cannot run the status loops, so it is not ready.
        HttpResponseMessage readyz = await _client.GetAsync("/readyz", TestContext.Current.CancellationToken);
        Assert.Equal(503, (int)readyz.StatusCode);

        HttpResponseMessage healthz = await _client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.Equal(503, (int)healthz.StatusCode);
    }

    [Fact]
    public async Task ContainerLogs_StreamsProviderLines()
    {
        Channel<string> lines = Channel.CreateBounded<string>(new BoundedChannelOptions(16) { SingleWriter = true });
        _podController.GetContainerLogs("default", "web", "web-container", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IAsyncEnumerable<string>>(ReadLines(lines, TestContext.Current.CancellationToken)));

        lines.Writer.TryWrite("first line");
        lines.Writer.TryWrite("second line");
        lines.Writer.TryComplete();

        HttpResponseMessage response =
            await _client.GetAsync("/containerLogs/default/web/web-container", TestContext.Current.CancellationToken);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        // The kubelet endpoint terminates each provider line; kubectl/k9s see complete lines.
        Assert.Equal("first line\nsecond line\n",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ContainerLogs_StopsProducing_WhenClientDisconnects()
    {
        List<string> produced = new();
        // Each call gets a fresh enumerable bound to the caller's token: the endpoint passes
        // the request's abort token, which is exactly what must stop the producer.
        _podController.GetContainerLogs("default", "web", "web-container", Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IAsyncEnumerable<string>>(
                ProduceLinesUntilCancelled(produced, ci.ArgAt<CancellationToken>(3))));

        // ResponseHeadersRead: the body streams until the provider completes or the client disconnects.
        using CancellationTokenSource requestCancellation = new();
        HttpResponseMessage response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/containerLogs/default/web/web-container"),
            HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token);
        Assert.Equal(200, (int)response.StatusCode);

        // Read the first streamed line, then drop the connection like a user closing k9s.
        using StreamReader reader = new(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        Assert.Equal("log line", await reader.ReadLineAsync(TestContext.Current.CancellationToken));

        // Drop the connection like a user closing k9s: cancel the request and dispose the
        // response so the socket actually closes and Kestrel fires the abort token.
        requestCancellation.Cancel();
        response.Dispose();

        // Kestrel's disconnect detection is asynchronous: poll until the provider reaches a
        // fixpoint (no more lines produced for the dead client) or give up.
        int previous = -1;
        int stableSamples = 0;
        for (int sample = 0; sample < 100 && stableSamples < 3; sample++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            if (produced.Count == previous)
            {
                stableSamples++;
            }
            else
            {
                stableSamples = 0;
                previous = produced.Count;
            }
        }
        // The provider stopped producing lines once the client went away.
        Assert.True(stableSamples >= 3, $"provider kept producing after disconnect (last count: {previous})");
        Assert.True(previous >= 1);
    }

    private static async IAsyncEnumerable<string> ReadLines(Channel<string> lines,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (string line in lines.Reader.ReadAllAsync(cancellationToken))
        {
            yield return line;
        }
    }

    private static async IAsyncEnumerable<string> ProduceLinesUntilCancelled(List<string> produced,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            produced.Add("log line");
            yield return "log line";
            await Task.Delay(10, cancellationToken);
        }
    }
}

/// <summary>
/// The /readyz and /healthz probes report ready once the replica's leader election has
/// synchronized: it holds the node lease itself (or has observed a peer holding it). The
/// test host runs a real <see cref="LeaderElectionService"/> against a mocked lease API.
/// </summary>
public class KubeletReadinessTests : IAsyncLifetime
{
    private readonly IPodController _podController = Substitute.For<IPodController>();
    private readonly MockCluster _cluster = MockCluster.CreateLeader();
    private LeaderElectionService _leaderElection = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _podController.GetPodsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IEnumerable<V1Pod>>(Array.Empty<V1Pod>()));
        _leaderElection = new LeaderElectionService(
            new SharpConfig { NodeName = "test-node" }, _cluster.Kubernetes,
            NullLogger<LeaderElectionService>.Instance);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(_podController);
        builder.Services.AddSingleton(new SharpConfig { NodeName = "test-node" });
        // Registered exactly like AddVirtualKubelet: the concrete type resolvable, the host
        // running the very instance that the probes and status services read.
        builder.Services.AddSingleton(_leaderElection);
        builder.Services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<LeaderElectionService>());
        _app = builder.Build();
        _app.MapKubeletEndpoints();
        await _app.StartAsync();

        IServerAddressesFeature addresses = _app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!;
        _client = new HttpClient { BaseAddress = new Uri(addresses.Addresses.First()) };
        await TestHelper.WaitUntilAsync(() => _leaderElection.IsLeader);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Readyz_ReturnsReady_OnceLeaderElectionSynchronizes()
    {
        HttpResponseMessage response = await _client.GetAsync("/readyz", TestContext.Current.CancellationToken);
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("\"ready\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Healthz_ReturnsReady_OnceLeaderElectionSynchronizes()
    {
        HttpResponseMessage response = await _client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.Equal(200, (int)response.StatusCode);
    }
}