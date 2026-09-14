using System.Security.Cryptography.X509Certificates;
using k8s;
using k8s.Models;
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
    /// Registers the virtual kubelet services (kubernetes client, leader election, hosted status
    /// services) and the Kestrel listeners (10255 http, 10250 https with client certs) on the
    /// builder. Call <see cref="MapKubeletEndpoints(WebApplication)"/> on the application after
    /// <c>Build()</c> to expose the kubelet API endpoints.
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="config"></param>
    /// <param name="configureProvider">
    /// Callback that registers the pod/node provider. Implement <see cref="IPodController"/> and
    /// <see cref="INodeController"/> (plus any supporting services) and add them here. A kubelet
    /// that registers neither fails to start: there is no built-in default provider.
    /// </param>
    /// <returns></returns>
    public static WebApplicationBuilder AddVirtualKubelet(this WebApplicationBuilder builder, SharpConfig config,
        Action<IServiceCollection>? configureProvider = null)
    {
        builder.ConfigureKubeletListeners();
        builder.AddVirtualKubeletServices(config, configureProvider);
        return builder;
    }

    /// <summary>
    /// Maps the kubelet API endpoints (<c>/pods</c>, <c>/runningpods</c>, <c>/stats/sum</c>,
    /// <c>/containerLogs/{namespace}/{pod}/{container}</c>) and the health probes (<c>/livez</c>,
    /// <c>/readyz</c>, <c>/healthz</c>) onto the application. The probes are designed for the
    /// read-only 10255 port: they require no client certificate. The pod and log endpoints serve
    /// the data reported by the registered <see cref="IPodController"/>;
    /// the API server proxies <c>kubectl get pods</c>/<c>kubectl logs</c> requests for pods on the
    /// virtual node to them. <c>/stats/sum</c> reports zero resource usage for the pods the
    /// provider knows about, which is what the reference implementation measures: providers that
    /// track real usage should map their own <c>/stats/sum</c>. The kubelet exec and port-forward
    /// endpoints are not implemented: pods on the virtual node are not real containers.
    /// </summary>
    public static WebApplication MapKubeletEndpoints(this WebApplication app)
    {
        app.MapGet("/containerLogs/{podNamespace}/{podID}/{containerName}",
            async (HttpContext context, IPodController podController,
                string podNamespace, string podID, string containerName,
                CancellationToken cancellationToken) =>
            {
                // Per-line flush keeps kubectl logs -f / k9s streaming live; Kestrel frames the
                // open-ended body as chunked. cancellationToken is the client disconnect: closing
                // k9s or Ctrl-C'ing kubectl stops the provider's enumeration.
                context.Response.ContentType = "text/plain";
                IAsyncEnumerable<string> lines = await podController.GetContainerLogs(
                    podNamespace, podID, containerName, cancellationToken);
                await foreach (string line in lines)
                {
                    await context.Response.WriteAsync($"{line}\n", cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                }

                await context.Response.CompleteAsync();
            });
        app.MapGet("/pods",
            async (IPodController podController, CancellationToken cancellationToken) =>
            {
                // The provider is the source of truth for the pods on the virtual node; the API
                // server proxies node /pods requests through here.
                IEnumerable<V1Pod> pods = await podController.GetPodsAsync(cancellationToken);
                return Results.Json(pods);
            });
        app.MapGet("/runningpods",
            async (IPodController podController, CancellationToken cancellationToken) =>
            {
                // Legacy kubelet endpoint: only pods the provider reports as Running are listed.
                IEnumerable<V1Pod> pods = await podController.GetPodsAsync(cancellationToken);
                List<V1Pod> running = pods.Where(pod => pod.Status is { Phase: "Running" }).ToList();
                return Results.Json(running);
            });
        app.MapGet("/stats/sum",
            async (IPodController podController, SharpConfig config, CancellationToken cancellationToken) =>
            {
                // The reference implementation runs nothing real, so every stat is zero; see the
                // type docs of <see cref="StatsSummary"/> for the provider-side contract.
                DateTimeOffset timestamp = DateTimeOffset.UtcNow;
                IEnumerable<V1Pod> pods = await podController.GetPodsAsync(cancellationToken);
                StatsSummary summary = new()
                {
                    Node = new NodeStats
                    {
                        Node = config.NodeName,
                        Timestamp = timestamp,
                        Cpu = new CpuStats { Time = timestamp },
                        Memory = new MemoryStats { Time = timestamp },
                        Network = new NetworkStats { Time = timestamp },
                    },
                    Pods = pods.Select(pod => new PodStats
                    {
                        PodRef = new PodReference
                        {
                            Reference = pod.Metadata,
                            Timestamp = timestamp,
                        },
                        Cpu = new CpuStats { Time = timestamp },
                        Memory = new MemoryStats { Time = timestamp },
                        Network = new NetworkStats { Time = timestamp },
                        Containers = pod.Spec?.Containers is { } containers
                            ? containers.Select(container => new ContainerStats
                            {
                                Name = container.Name,
                                Cpu = new CpuStats { Time = timestamp },
                                Memory = new MemoryStats { Time = timestamp },
                                Rootfs = new FilesystemStats { Device = "rootfs", Time = timestamp },
                                Logs = new FilesystemStats { Device = "logs", Time = timestamp },
                            }).ToList()
                            : new List<ContainerStats>(),
                    }).ToList(),
                };
                return Results.Json(summary);
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
                if (!File.Exists(certPath) || !File.Exists(keyPath))
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
    
    private static WebApplicationBuilder AddVirtualKubeletServices(this WebApplicationBuilder collection,
        SharpConfig configuration, Action<IServiceCollection>? configureProvider)
    {
        KubernetesClientConfiguration kubernetesConfig = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        collection.Services.AddSingleton<IKubernetes>(_ => new Kubernetes(kubernetesConfig));
        // Consumer registration point: pass a callback to register the pod/node provider
        // (implement IPodController / INodeController plus any supporting services).
        configureProvider?.Invoke(collection.Services);
        // A virtual kubelet cannot run without a provider. Fail fast at construction time with an
        // actionable message instead of a null reference deep inside the status services.
        List<Type> candidates = new()
        {
            typeof(IPodController),
            typeof(INodeController),
        };
        List<Type> missing = candidates
            .Where(serviceType => !collection.Services.Any(descriptor => descriptor.ServiceType == serviceType))
            .ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"the virtual kubelet has no {string.Join(", ", missing.Select(serviceType => serviceType.Name))} registered. " +
                "Implement the interface(s) and register them via the configureProvider callback of AddVirtualKubelet " +
                "(Sharplet.Provider.Mock is the reference layout; see Sharplet.Samplekubelet's Program.cs)");
        }

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
