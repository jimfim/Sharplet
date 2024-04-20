using Sharplet.Core;

var builder = WebApplication.CreateBuilder(args);
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
});

var app = builder.Build();
app.Run();
