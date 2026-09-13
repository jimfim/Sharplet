using System.Text.Json;
using k8s.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
}