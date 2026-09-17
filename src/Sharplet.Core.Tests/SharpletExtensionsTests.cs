using k8s;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Sharplet.Core.Tests;

/// <summary>
/// Contract tests for <c>AddVirtualKubelet</c> service registration: a virtual kubelet with
/// no provider fails fast with an actionable message, and a registered provider plus the
/// kubelet's own services (kubernetes client, leader election, status loops, event watcher)
/// are all resolvable without starting the host.
/// </summary>
public class SharpletExtensionsTests
{
    [Fact]
    public void AddVirtualKubelet_WithoutProvider_ThrowsNamingMissingInterfaces()
    {
        using TemporaryKubeConfig kubeConfig = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => builder.AddVirtualKubelet(new SharpConfig { NodeName = "test-node" }));

        Assert.Contains("IPodController", exception.Message);
        Assert.Contains("INodeController", exception.Message);
    }

    [Fact]
    public void AddVirtualKubelet_WithMissingNodeProvider_ThrowsNamingOnlyTheMissingOne()
    {
        using TemporaryKubeConfig kubeConfig = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddVirtualKubelet(new SharpConfig { NodeName = "test-node" }, services =>
            {
                services.AddSingleton(Substitute.For<IPodController>());
            }));

        Assert.Contains("INodeController", exception.Message);
        Assert.DoesNotContain("IPodController", exception.Message);
    }

    [Fact]
    public void AddVirtualKubelet_WithProvider_RegistersKubeletServices()
    {
        using TemporaryKubeConfig kubeConfig = new();
        IPodController podController = Substitute.For<IPodController>();
        INodeController nodeController = Substitute.For<INodeController>();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        builder.AddVirtualKubelet(new SharpConfig { NodeName = "test-node" }, services =>
        {
            services.AddSingleton(podController);
            services.AddSingleton(nodeController);
        });

        Assert.Contains(builder.Services, d => d.ServiceType == typeof(IPodController)
            && d.ImplementationInstance == podController);
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(INodeController)
            && d.ImplementationInstance == nodeController);
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(IKubernetes));
        // The concrete type is resolvable so the status services see the very instance that runs the election.
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(LeaderElectionService));
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(IHostedService));
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(IEventWatcher));
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(SharpConfig));
    }
}