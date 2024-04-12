using Microsoft.Extensions.Hosting;

namespace Sharplet.Core;

public class EventWatcherService : BackgroundService
{
    private readonly IEventWatcher _eventWatcher;

    public EventWatcherService(IEventWatcher eventWatcher)
    {
        _eventWatcher = eventWatcher;
    }

    public override async Task<Task> StartAsync(CancellationToken cancellationToken)
    {
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _eventWatcher.WatchEventStream();
    }

    public override async Task<Task> StopAsync(CancellationToken cancellationToken)
    {
        return base.StopAsync(cancellationToken);
    }
}