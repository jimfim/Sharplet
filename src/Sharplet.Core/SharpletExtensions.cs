using System.Security.Cryptography.X509Certificates;
using k8s;
using k8s.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sharplet.Core;

public static class SharpletExtensions
{
    /// <summary>
    /// Registers the virtual kubelet services (kubernetes client, leader election, hosted status
    /// services) and the Kestrel listeners (10255 http, 10250 https with client certs) on the
    /// builder. Call <see cref="MapKubeletEndpoints(WebApplication)"/> on the application after
    /// <c>Build()</c> to expose the kubelet API endpoints.
    /// </summary>
    /// <para>
    /// The 10250 listener requires a client certificate (mutual TLS); callers without one are
    /// rejected. When a client CA bundle is available — the <c>SHARPLET_CLIENT_CA</c> environment
    /// variable pointing at a bundle file, or in-cluster the cluster CA mounted for the pod's
    /// service account — presented certificates are verified against it. Without a bundle the
    /// listener still requires a certificate but cannot verify the chain (any presented
    /// certificate is accepted) and logs the gap at critical level on startup.
    /// </para>
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

    private static void ConfigureKubeletListeners(this WebApplicationBuilder builder)
    {
        // 10250 is the API server's mutual-TLS listener: every caller must present a client
        // certificate. When a client CA bundle is available the presented certificate is
        // verified against it; without one the chain cannot be verified and the gap is
        // flagged at critical level (a certificate is still required, but any is accepted).
        X509Certificate2Collection? clientCa = ResolveClientCertificateAuthority();
        // When no client CA is available the listener requires a client certificate but
        // cannot verify it; the status service flags that gap at startup.
        builder.Services.AddSingleton<IHostedService>(provider =>
            new ClientCaStatusService(provider.GetRequiredService<ILogger<ClientCaStatusService>>(), clientCa is not null));

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
                    adapterOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    if (clientCa is not null)
                    {
                        // Verify the presented certificate against the client CA bundle. The
                        // chain is built fresh, skipping revocation checks (a virtual kubelet
                        // cannot depend on the cluster's CRL/OCSP infrastructure). A CA that is
                        // not in the OS trust store reports UntrustedRoot on every platform, so
                        // trust is anchored by membership: the chain must terminate at one of
                        // the bundled CAs and carry no other structural faults.
                        adapterOptions.ClientCertificateValidation = (certificate, chain, sslErrors) =>
                        {
                            if (certificate is null)
                            {
                                return false;
                            }
                            X509Chain validationChain = new();
                            validationChain.ChainPolicy.ExtraStore.AddRange(clientCa);
                            validationChain.ChainPolicy.VerificationFlags =
                                X509VerificationFlags.IgnoreEndRevocationUnknown |
                                X509VerificationFlags.IgnoreCtlSignerRevocationUnknown |
                                X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown |
                                X509VerificationFlags.IgnoreRootRevocationUnknown;
                            validationChain.Build(certificate);
                            X509ChainStatusFlags ignored =
                                X509ChainStatusFlags.UntrustedRoot |
                                X509ChainStatusFlags.RevocationStatusUnknown |
                                X509ChainStatusFlags.OfflineRevocation |
                                X509ChainStatusFlags.NoIssuanceChainPolicy;
                            X509ChainStatusFlags faults = 0;
                            foreach (X509ChainStatus status in validationChain.ChainStatus)
                            {
                                faults |= status.Status;
                            }
                            if ((faults & ~ignored) != 0)
                            {
                                return false;
                            }
                            return validationChain.ChainElements.Any(element =>
                                clientCa.Any(trusted => trusted.Thumbprint == element.Certificate.Thumbprint));
                        };
                    }
                    else
                    {
                        // No client CA to verify against: any presented certificate is
                        // accepted (flagged at startup), but callers are never anonymous.
                        adapterOptions.ClientCertificateValidation = (certificate, chain, sslErrors) => true;
                    }
                });
            });
        });
    }

    /// <summary>
    /// Resolves the client CA bundle for the 10250 listener: the <c>SHARPLET_CLIENT_CA</c>
    /// environment variable pointing at a bundle file wins; otherwise the cluster CA
    /// mounted for the pod's service account (the standard in-cluster location,
    /// overridable via KUBERNETES_ROOT_CA_FILE_PATH) — the same bundle the kubernetes
    /// client trusts. Null when neither is available.
    /// </summary>
    private static X509Certificate2Collection? ResolveClientCertificateAuthority()
    {
        string? caPath = Environment.GetEnvironmentVariable("SHARPLET_CLIENT_CA");
        if (string.IsNullOrWhiteSpace(caPath))
        {
            // In-cluster fallback: the cluster CA mounted for the pod's service account.
            caPath = Environment.GetEnvironmentVariable("KUBERNETES_ROOT_CA_FILE_PATH")
                ?? "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";
        }
        if (!File.Exists(caPath))
        {
            return null;
        }
        return CreateCaCollection(File.ReadAllText(caPath));
    }

    /// <summary>
    /// Parses a PEM bundle: every CERTIFICATE block becomes a member of the collection.
    /// </summary>
    private static X509Certificate2Collection? CreateCaCollection(string caPem)
    {
        X509Certificate2Collection collection = new();
        const string begin = "-----BEGIN CERTIFICATE-----";
        const string end = "-----END CERTIFICATE-----";
        int start = 0;
        while ((start = caPem.IndexOf(begin, start, StringComparison.Ordinal)) >= 0)
        {
            int blockEnd = caPem.IndexOf(end, start, StringComparison.Ordinal);
            if (blockEnd < 0)
            {
                break;
            }
            collection.Add(X509Certificate2.CreateFromPem(caPem.Substring(start, blockEnd + end.Length - start)));
            start = blockEnd + end.Length;
        }
        return collection.Count > 0 ? collection : null;
    }

    private static void AddVirtualKubeletServices(this WebApplicationBuilder collection,
        SharpConfig configuration, Action<IServiceCollection>? configureProvider)
    {
        // Consumer registration point: pass a callback to register the pod/node provider
        // (implement IPodController / INodeController plus any supporting services).
        configureProvider?.Invoke(collection.Services);
        // A virtual kubelet cannot run without a provider. Fail fast at construction time with an
        // actionable message instead of a null reference deep inside the status services. Validated
        // before the kubernetes client configuration so the contract holds on machines without
        // any kubeconfig at all.
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

        // In-cluster is the production path (the kubelet's service account). Outside a cluster
        // BuildDefaultConfig resolves the standard KUBECONFIG override, then ~/.kube/config
        // (BuildConfigFromConfigFile would ignore KUBECONFIG entirely).
        KubernetesClientConfiguration kubernetesConfig = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildDefaultConfig();
        collection.Services.AddSingleton<IKubernetes>(_ => new Kubernetes(kubernetesConfig));

        // Registered explicitly (not via AddHostedService<T>) because the web host builder
        // only records the IHostedService alias: the concrete type must be resolvable so the
        // status services can read leadership state from the very instance that runs it.
        collection.Services.AddSingleton<LeaderElectionService>();
        collection.Services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<LeaderElectionService>());
        collection.Services.AddHostedService<NodeControllerService>();
        collection.Services.AddHostedService<EventWatcherService>();
        collection.Services.AddHostedService<PodControllerService>();
        collection.Services.AddSingleton(configuration);
        collection.Services.AddSingleton<PodErrorReporter>();
        collection.Services.AddSingleton<IEventWatcher, EventWatcher>();
    }
}

public class SharpConfig
{
    public string NodeName { get; set; } = "sharplet";
    public int PodStatusUpdateInterval { get; set; } = 10;
    public int NodeStatusUpdateInterval { get; set; } = 30;
    public int NodeMaxPodCount { get; set; } = 10;
}
