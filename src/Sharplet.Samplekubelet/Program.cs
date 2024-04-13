using System.Net;
using System.Security.Cryptography.X509Certificates;
using k8s;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Sharplet.Core;
using Sharplet.Provider.Mock;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddKubelet(new SharpConfig
{
 NodeName = "sharplet",
 PodStatusUpdateInterval = 15,
 NodeStatusUpdateInterval = 30,
 NodeMaxPodCount = 6
});
builder.Logging.AddJsonConsole();
builder.Services.AddSingleton<IPodController, MockPodController>();
builder.Services.AddSingleton<INodeController, MockNodeController>();
builder.Configuration.SetBasePath(Directory.GetCurrentDirectory());
builder.Configuration.AddEnvironmentVariables();
var config = KubernetesClientConfiguration.IsInCluster()
    ? KubernetesClientConfiguration.InClusterConfig()
    : KubernetesClientConfiguration.BuildConfigFromConfigFile();
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(10255);
    options.ListenAnyIP(10250, listenOptions =>
    {
        var cert = File.ReadAllText("/etc/virtual-kubelet/cert.pem"); 
        var key = File.ReadAllText("/etc/virtual-kubelet/key.pem");
        var x509 = X509Certificate2.CreateFromPem(cert, key);
        listenOptions.UseHttps(adapterOptions =>
        {
            adapterOptions.ServerCertificate = x509;
            adapterOptions.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            adapterOptions.ClientCertificateValidation = (certificate, chain, valid) => true;
        });    
    });
});

builder.Services.AddSingleton<IKubernetes>(_ => new Kubernetes(config));
var app = builder.Build();
// app.MapGet("/", () => "Hello World!");
// app.MapGet("/metrics", () => "Hello World!");
// app.MapGet("/metrics/cadvisor", () => "Hello World!");
// app.MapGet("/metrics/resource", () => "Hello World!");
// app.MapGet("/metrics/probes", () => "Hello World!");
//
// app.MapGet("/stats/", () => "Hello World!");
// app.MapGet("/logs/", () => "Hello World!");
//
// app.MapGet("/debug/pprof/", () => "Hello World!");
// app.MapGet("/debug/flags/v", () => "Hello World!");
//
// app.MapGet("/pods", () => "Hello World!");
//
// app.MapGet("/run/{podNamespace}/{podID}/{containerName}", () => "Hello World!");
//
// app.MapGet("/exec/{podNamespace}/{podID}/{containerName}", () => "Hello World!");
// app.MapPost("/exec/{podNamespace}/{podID}/{containerName}", () => "Hello World!");
//
// app.MapGet("/attach/{podNamespace}/{podID}/{containerName}", () => "Hello World!");
// app.MapPost("/attach/{podNamespace}/{podID}/{containerName}", () => "Hello World!");
//
// app.MapGet("/portforward/{podNamespace}/{podID}/{uid}", () => "Hello World!");
// app.MapGet("/portforward/{podNamespace}/{podID}/{containerName}", () => "Hello World!");
// app.MapPost("/portforward/{podNamespace}/{podID}/{uid}", () => "Hello World!");
// app.MapGet("/portforward/{podNamespace}/{podID}", () => "Hello World!");
// app.MapPost("/portforward/{podNamespace}/{podID}", () => "Hello World!");
//app.MapGet("/containerLogs/{podNamespace}/{podID}/{containerName}", () => "Hello World!");
app.MapGet("/containerLogs/{podNamespace}/{podID}/{containerName}", 
    async (HttpContext context, string podNamespace, string podID, string containerName) =>
{
    var service = app.Services.GetRequiredService<IPodController>();
    context.Response.Headers.Append("Content-Type", "text/plain");
    context.Response.Headers.Append("Transfer-Encoding", "chunked");
    await foreach (var logEntry in await service.GetContainerLogs(podNamespace, podID, containerName, default))
    {
        await context.Response.WriteAsync(logEntry);
        await context.Response.Body.FlushAsync();
    }
    await context.Response.CompleteAsync();
});

app.MapGet("/configz", () => "Hello World!");

app.MapGet("/runningpods", () => "Hello World!");
app.Run();
