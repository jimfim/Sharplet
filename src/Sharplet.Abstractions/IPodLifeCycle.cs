// See https://aka.ms/new-console-template for more information

using k8s.Models;

namespace Sharplet.Abstractions;

public interface IPodLifeCycle
{
    Task CreatePodAsync(V1Pod pod, CancellationToken cancellationToken = default);
    Task UpdatePodAsync(V1Pod pod, CancellationToken cancellationToken = default);
    Task DeletePodAsync(V1Pod pod, CancellationToken cancellationToken = default);
    Task<V1Pod?> GetPodAsync(string @namespace, string name, CancellationToken cancellationToken = default);

    Task<V1PodStatus> GetPodStatusAsync(string @namespace, string name, CancellationToken cancellationToken = default);
    Task<IEnumerable<V1Pod>> GetPodsAsync(CancellationToken cancellationToken = default);
}