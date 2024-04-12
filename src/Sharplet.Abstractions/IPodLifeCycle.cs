// See https://aka.ms/new-console-template for more information

using k8s.Models;

namespace Sharplet.Abstractions;

// This interface provides methods for managing the lifecycle of pods on a virtual node.
public interface IPodLifeCycle
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
    /// Deletes an existing pod from the virtual node.
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
}
