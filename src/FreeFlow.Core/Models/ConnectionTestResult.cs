namespace FreeFlow.Core.Models;

public sealed class ConnectionTestResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}
