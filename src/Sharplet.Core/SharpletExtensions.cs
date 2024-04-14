using Microsoft.Extensions.DependencyInjection;

namespace Sharplet.Core;

public static class SharpletExtensions
{
    public static IServiceCollection AddKubelet(this IServiceCollection collection, SharpConfig configuration)
    {
        collection.AddSingleton<IPodController, MockPodController>();
        collection.AddSingleton<INodeController, MockNodeController>();
        collection.AddHostedService<NodeControllerService>();
        collection.AddHostedService<EventWatcherService>();
        collection.AddHostedService<PodControllerService>();
        collection.AddSingleton(configuration);
        collection.AddSingleton<IEventWatcher, EventWatcher>();
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
