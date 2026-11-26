using System.Globalization;

namespace Modules.Siigo.Application.Invoicing;

/// <summary>
/// Spec 2026-11-20 §4.4: el vendedor de la factura es el usuario de Siigo cuyo correo es el del asesor del pedido. No se configura en
/// la conexión, así que cada venta queda a nombre de quien la hizo. Sin coincidencia no hay vendedor (y la factura no está lista): un
/// vendedor por defecto le cargaría la venta a otra persona. Dos usuarios con el mismo correo no deberían existir; si existen, gana el
/// de menor id para que el resultado no dependa del orden en que Siigo los devuelve.
/// </summary>
public static class SiigoSellerResolver
{
    /// <param name="users">Los usuarios activos de Siigo (<see cref="ISiigoApi.ListUsersAsync"/> ya deja fuera los inactivos).</param>
    public static string? Resolve(string? advisorEmail, IReadOnlyList<SiigoUser> users)
    {
        ArgumentNullException.ThrowIfNull(users);
        var email = advisorEmail?.Trim();
        if (string.IsNullOrEmpty(email))
        {
            return null;
        }

        return users
            .Where(user => string.Equals(user.Email?.Trim(), email, StringComparison.OrdinalIgnoreCase))
            .OrderBy(user => long.TryParse(user.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : long.MaxValue)
            .ThenBy(user => user.Id, StringComparer.Ordinal)
            .Select(user => user.Id)
            .FirstOrDefault();
    }
}
