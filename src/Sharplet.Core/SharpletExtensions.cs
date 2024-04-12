using Microsoft.Extensions.DependencyInjection;

namespace Sharplet.Core;

public static class SharpletExtensions
{
    public static IServiceCollection AddKubelet(this IServiceCollection collection, SharpConfig configuration)
    {
        collection.AddHostedService<NodeControllerService>();
        collection.AddHostedService<EventWatcherService>();
        collection.AddHostedService<PodStatusTrackerService>();
        collection.AddSingleton(configuration);
        collection.AddSingleton<INodeController, NodeController>();
        collection.AddSingleton<IEventWatcher, EventWatcher>();
        return collection;
    }
}

public class SharpConfig
{
    public string NodeName { get; set; } = "sharplet";
    public int StatusUpdateInterval { get; set; } = 10;
}