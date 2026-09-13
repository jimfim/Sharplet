using Sharplet.Core;
using Sharplet.Provider.Mock;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole();
builder.Logging.AddConsole();
builder.Configuration.SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json")
    .AddJsonFile($"appsettings.{builder.Environment}.json", optional: true, true);
builder.Configuration.AddEnvironmentVariables();
builder.AddVirtualKubelet(new SharpConfig
{
    NodeName = "sharplet",
    PodStatusUpdateInterval = 15,
    NodeStatusUpdateInterval = 30,
    NodeMaxPodCount = 6
}, services =>
{
    // The sample kubelet runs the reference (mock) provider.
    services.AddSingleton<IPodController, MockPodController>();
    services.AddSingleton<INodeController, MockNodeController>();
});

WebApplication app = builder.Build();
app.MapKubeletEndpoints();
app.Run();