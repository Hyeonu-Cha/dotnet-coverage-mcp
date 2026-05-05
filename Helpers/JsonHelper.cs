using System.Text.Json;

namespace DotNetCoverageMcp.Helpers;

public static class JsonHelper
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Error(string errorType, string message) =>
        JsonSerializer.Serialize(new { error = message, errorType }, Options);

    public static string Serialize(object value) =>
        JsonSerializer.Serialize(value, Options);
}
