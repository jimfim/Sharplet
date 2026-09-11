using System.Security.Cryptography.X509Certificates;
using k8s;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Sharplet.Core;

public static class SharpletExtensions
{
    /// <summary>
    /// Registers the virtual kubelet services (kubernetes client, controllers, hosted services)
    /// and the Kestrel listeners (10255 http, 10250 https with client certs) on the builder.
    /// Call <see cref="MapKubeletEndpoints(WebApplication)"/> on the application after
    /// <c>Build()</c> to expose the kubelet API endpoints.
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="config"></param>
    /// <returns></returns>
    public static WebApplicationBuilder AddVirtualKubelet(this WebApplicationBuilder builder, SharpConfig config)
    {
        builder.ConfigureKubeletListeners();
        builder.AddVirtualKubeletServices(config);
        return builder;
    }

    /// <summary>
    /// Maps the kubelet API endpoints (<c>/containerLogs/{namespace}/{pod}/{container}</c>) and the
    /// health probes (<c>/livez</c>, <c>/readyz</c>, <c>/healthz</c>) onto the application. The probes
    /// are designed for the read-only 10255 port: they require no client certificate.
    /// </summary>
    /// <param name="app"></param>
    /// <returns></returns>
    public static WebApplication MapKubeletEndpoints(this WebApplication app)
    {
        app.MapGet("/containerLogs/{podNamespace}/{podID}/{containerName}",
            async (HttpContext context, string podNamespace, string podID, string containerName) =>
            {
                Random random = new();
                context.Response.Headers.Append("Content-Type", "text/plain");
                context.Response.Headers.Append("Transfer-Encoding", "chunked");
                for (int i = 0; i < 10; i++)
                {
                    string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {podNamespace} {podID} {containerName} Log message {i}\n";
                    await context.Response.WriteAsync(logMessage);
                    await context.Response.Body.FlushAsync();
                    await Task.Delay(random.Next(1000, 3000)); // Simulate delays between log messages
                }

                await context.Response.CompleteAsync();
            });
        app.MapGet("/livez", () => Results.Ok("alive"));
        app.MapGet("/readyz", (IServiceProvider services) => KubeletReadyResult(services));
        app.MapGet("/healthz", (IServiceProvider services) => KubeletReadyResult(services));
        return app;
    }

    private static IResult KubeletReadyResult(IServiceProvider services)
    {
        // Ready once the replica is synchronized with the node lease — either it is the
        // leader itself or it has observed a leader. Until then it cannot safely run the
        // kubelet status loops, so it must not count as available.
        LeaderElectionService? election = services.GetService<LeaderElectionService>();
        return election is not null && (election.IsLeader || election.LeaderIdentity is not null)
            ? Results.Ok("ready")
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    private static WebApplicationBuilder ConfigureKubeletListeners(this WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(10255);
            options.ListenAnyIP(10250, listenOptions =>
            {
                string certPath = Environment.GetEnvironmentVariable("APISERVER_CERT_LOCATION") ?? "/etc/sharplet/cert.pem";
                string keyPath = Environment.GetEnvironmentVariable("APISERVER_KEY_LOCATION") ?? "/etc/sharplet/key.pem";
                if (File.Exists(certPath) is false || File.Exists(keyPath) is false)
                {
                    // Local-debug fallback: the CSR tool writes to ~/.sharplet when /etc/sharplet is not writable.
                    string homeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sharplet");
                    string homeCert = Path.Combine(homeDir, "cert.pem");
                    string homeKey = Path.Combine(homeDir, "key.pem");
                    if (File.Exists(homeCert) && File.Exists(homeKey))
                    {
                        certPath = homeCert;
                        keyPath = homeKey;
                    }
                }
                string cert = File.ReadAllText(certPath);
                string key = File.ReadAllText(keyPath);
                X509Certificate2 x509 = X509Certificate2.CreateFromPem(cert, key);
                listenOptions.UseHttps(adapterOptions =>
                {
                    adapterOptions.ServerCertificate = x509;
                    adapterOptions.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                    adapterOptions.ClientCertificateValidation = (certificate, chain, valid) => true;
                });
            });
        });
        return builder;
    }
    
    private static WebApplicationBuilder AddVirtualKubeletServices(this WebApplicationBuilder collection, SharpConfig configuration)
    {
        var config = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        collection.Services.AddSingleton<IKubernetes>(_ => new Kubernetes(config));
        collection.Services.AddSingleton<IPodController, MockPodController>();
        collection.Services.AddSingleton<INodeController, MockNodeController>();
        // Registered explicitly (not via AddHostedService<T>) because the web host builder
        // only records the IHostedService alias: the concrete type must be resolvable so the
        // status services can read leadership state from the very instance that runs it.
        collection.Services.AddSingleton<LeaderElectionService>();
        collection.Services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<LeaderElectionService>());
        collection.Services.AddHostedService<NodeControllerService>();
        collection.Services.AddHostedService<EventWatcherService>();
        collection.Services.AddHostedService<PodControllerService>();
        collection.Services.AddSingleton(configuration);
        collection.Services.AddSingleton<IEventWatcher, EventWatcher>();
        return collection;
    }
}

public class SharpConfig
{
    public string NodeName { get; set; } = "sharplet";
    public int PodStatusUpdateInterval { get; set; } = 10;
    public int NodeStatusUpdateInterval { get; set; } = 30;
    public int NodeMaxPodCount { get; set; } = 10;
}
