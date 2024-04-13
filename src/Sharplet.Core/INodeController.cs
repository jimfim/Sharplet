
using k8s.Models;

namespace Sharplet.Core;

/// <summary>
/// Represents a controller for managing virtual nodes
/// </summary>
public interface INodeController
{
  /// <summary>
    /// Creates a new node with the given name and configuration.
    /// </summary>
    /// <param name="node">The node to be created.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task CreateNodeAsync(V1Node node, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a node with the given name.
    /// </summary>
    /// <param name="nodeName">The name of the node to retrieve.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation, returning the node with the given name.</returns>
    Task<V1Node> GetNodeAsync(string nodeName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a node with the given name.
    /// </summary>
    /// <param name="nodeName">The name of the node to delete.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task DeleteNodeAsync(string nodeName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the status of a node with the given name.
    /// </summary>
    /// <param name="nodeName">The name of the node to retrieve.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation, returning the status of the node with the given name.</returns>
    Task<V1NodeStatus> GetNodeStatusAsync(string nodeName, CancellationToken cancellationToken = default);
}