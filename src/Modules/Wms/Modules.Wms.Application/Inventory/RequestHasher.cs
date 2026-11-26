using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Modules.Wms.Application;

public static class RequestHasher
{
    /// <summary>SHA-256 en hex minúscula de <c>"{método} {ruta}\n{JSON del cuerpo}"</c>. Se hashea el DTO ya enlazado y no los bytes
    /// del request: dos cuerpos equivalentes con las propiedades en otro orden son la misma operación.</summary>
    public static string Hash(string method, string path, object? body)
    {
        var text = $"{method} {path}\n{JsonSerializer.Serialize(body, JsonSerializerOptions.Web)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
