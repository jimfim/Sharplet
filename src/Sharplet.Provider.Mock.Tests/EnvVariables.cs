namespace Sharplet.Provider.Mock.Tests;

/// <summary>
/// Pins <c>VKUBELET_POD_IP</c> and <c>POD_IP</c> for the duration of a test; a
/// <c>null</c> value clears the variable. Previous values are restored on dispose. Both
/// test classes in this project read these variables, so they run serialized in the
/// <c>environment</c> collection.
/// </summary>
internal sealed class EnvVariables : IDisposable
{
    private readonly (string Name, string? Previous)[] _previous;
    private bool _disposed;

    private EnvVariables(string[] names, (string Name, string? Value)[] assign)
    {
        _previous = names.Select(name => (name, Environment.GetEnvironmentVariable(name))).ToArray();
        foreach ((string name, string? value) in assign)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public static EnvVariables Scope(string? kubeletIp = null, string? podIp = null)
    {
        string[] names = { "VKUBELET_POD_IP", "POD_IP" };
        (string Name, string? Value)[] assign =
        {
            ("VKUBELET_POD_IP", kubeletIp),
            ("POD_IP", podIp)
        };
        return new EnvVariables(names, assign);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach ((string name, string? previous) in _previous)
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}