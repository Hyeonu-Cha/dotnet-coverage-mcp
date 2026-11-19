using System.Text.Encodings.Web;
using System.Text.Json;

namespace DotNetCoverageMcp.Helpers;

public static class JsonHelper
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // The default encoder escapes < > & ' " and every non-ASCII character for HTML safety:
        // a C# snippet's `List<T>` came back as `List<T>`, and each character of a
        // Korean folder name as \uXXXX. These responses are read by a model and never embedded
        // in HTML, so only what JSON itself requires is escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Error(string errorType, string message) =>
        JsonSerializer.Serialize(new { error = message, errorType }, Options);

    public static string Serialize(object value) =>
        JsonSerializer.Serialize(value, Options);
}
