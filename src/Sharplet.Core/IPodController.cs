using k8s.Models;

namespace Sharplet.Core;

/// <summary>
/// This interface provides methods for managing the lifecycle of pods on a virtual node.
/// </summary>
public interface IPodController
{
    /// <summary>
    /// Creates a new pod and schedules it onto the virtual node.
    /// </summary>
    /// <param name="pod"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task CreatePodAsync(V1Pod pod, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing pod on the virtual node.
    /// </summary>
    /// <param name="pod"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task UpdatePodAsync(V1Pod pod, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an existing pod from the virtual node. The pod object has already been deleted from
    /// the API server by the user or controller; implementations must only release local state
    /// (containers, resources) and must not delete the API object.
    /// </summary>
    /// <param name="pod"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task DeletePodAsync(V1Pod pod, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an existing pod by namespace and name.
    /// </summary>
    /// <param name="namespace"></param>
    /// <param name="name"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<V1Pod?> GetPodAsync(string @namespace, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the status of an existing pod by namespace and name.
    /// </summary>
    /// <param name="namespace"></param>
    /// <param name="name"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<V1PodStatus> GetPodStatusAsync(string @namespace, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a list of all pods currently running on the virtual node.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<V1Pod>> GetPodsAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Gets the log output of a container on the virtual node. Each yielded string is one line of
    /// log without a trailing newline; the kubelet's <c>/containerLogs</c> endpoint terminates the
    /// lines when streaming them to pod-log API clients (kubectl, k9s, ...). The kubelet passes
    /// the client-disconnect token, so implementations should stop producing lines when it fires.
    /// </summary>
    /// <param name="namespace"></param>
    /// <param name="podname"></param>
    /// <param name="containername"></param>
    /// <param name="cancellationToken">Fires when the log client disconnects.</param>
    /// <returns></returns>
    Task<IAsyncEnumerable<string>> GetContainerLogs(string @namespace, string podname, string containername,
        CancellationToken cancellationToken);
}
