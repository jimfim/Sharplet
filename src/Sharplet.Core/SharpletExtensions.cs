using System.Security.Cryptography.X509Certificates;
using k8s;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Sharplet.Core;

public static class SharpletExtensions
{
    public static WebApplicationBuilder AddVirtualKubelet(this WebApplicationBuilder builder, SharpConfig config)
    {
        builder.ConfigureKubeletEndpoints(config);
        builder.AddVirtualKubeletServices(config);
        return builder;
    }

    private static WebApplicationBuilder ConfigureKubeletEndpoints(this WebApplicationBuilder builder,
        SharpConfig configuration)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(10255);
            options.ListenAnyIP(10250, listenOptions =>
            {
                var cert = File.ReadAllText("/etc/sharplet/cert.pem"); 
                var key = File.ReadAllText("/etc/sharplet/key.pem");
                var x509 = X509Certificate2.CreateFromPem(cert, key);
                listenOptions.UseHttps(adapterOptions =>
                {
                    adapterOptions.ServerCertificate = x509;
                    adapterOptions.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                    adapterOptions.ClientCertificateValidation = (certificate, chain, valid) => true;
                });    
            });
        });
        var app = builder.Build();
        app.MapGet("/containerLogs/{podNamespace}/{podID}/{containerName}", 
            async (HttpContext context, string podNamespace, string podID, string containerName) =>
            {
                var random = new Random();
                context.Response.Headers.Append("Content-Type", "text/plain");
                context.Response.Headers.Append("Transfer-Encoding", "chunked");
                for (var i = 0; i < 10; i++)
                {
                    var logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} - {podNamespace} {podID} {containerName} Log message {i}\n";
                    await context.Response.WriteAsync(logMessage);
                    await context.Response.Body.FlushAsync();
                    await Task.Delay(random.Next(1000, 3000)); // Simulate delays between log messages
                }

                await context.Response.CompleteAsync();
            });
        app.Run();
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
