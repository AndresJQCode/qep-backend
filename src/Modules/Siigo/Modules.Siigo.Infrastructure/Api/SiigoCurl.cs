using System.Text;
using System.Text.RegularExpressions;

namespace Modules.Siigo.Infrastructure.Api;

/// <summary>
/// El <c>curl</c> de una petición a Siigo, para diagnosticar. El token sale como <c>&lt;TOKEN&gt;</c> y la <c>access_key</c> de <c>/auth</c>
/// como <c>&lt;ACCESS_KEY&gt;</c>: para repetir la llamada se pide un token nuevo. El cuerpo va tal cual (lleva datos del cliente).
/// </summary>
internal static partial class SiigoCurl
{
    public static string Build(HttpRequestMessage request, string? body)
    {
        var curl = new StringBuilder("curl.exe -X ").Append(request.Method.Method).Append(" \"").Append(request.RequestUri).Append('"');
        foreach (var header in request.Headers)
        {
            var value = header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                ? "Bearer <TOKEN>"
                : string.Join(", ", header.Value);
            curl.Append(" -H \"").Append(header.Key).Append(": ").Append(value).Append('"');
        }

        if (body is not null)
        {
            curl.Append(" -H \"Content-Type: application/json\" -d '").Append(AccessKey().Replace(body, "\"access_key\":\"<ACCESS_KEY>\"")).Append('\'');
        }

        return curl.ToString();
    }

    [GeneratedRegex("\"access_key\"\\s*:\\s*\"[^\"]*\"")]
    private static partial Regex AccessKey();
}
