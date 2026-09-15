using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sharplet.Core;

/// <summary>
/// Flags at startup, at critical level, when the 10250 listener cannot verify client
/// certificate chains: without a client CA bundle (SHARPLET_CLIENT_CA, or in-cluster the
/// service account CA) any presented certificate is accepted, so the deployment must fix
/// the configuration before mutual TLS actually authorizes anything.
/// </summary>
internal class ClientCaStatusService : IHostedService
{
    private readonly ILogger<ClientCaStatusService> _logger;
    private readonly bool _hasClientCa;

    public ClientCaStatusService(ILogger<ClientCaStatusService> logger, bool hasClientCa)
    {
        _logger = logger;
        _hasClientCa = hasClientCa;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_hasClientCa)
        {
            _logger.LogCritical(
                "no client certificate authority is configured (set SHARPLET_CLIENT_CA to a CA bundle file, or run in a cluster with the service account CA mounted): the 10250 listener requires a client certificate but cannot verify it against a cluster CA, so any presented certificate is accepted");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}