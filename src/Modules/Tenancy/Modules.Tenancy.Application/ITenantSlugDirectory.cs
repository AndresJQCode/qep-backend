using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

/// <summary>
/// Consulta de sólo lectura: el tenant dueño de un slug (plan Soporte fase 3, F2). Puerto propio y no un método más en
/// <see cref="ITenantDirectory"/>, para no romper cada doble que lo implementa. Lo usa el formulario público de Soporte, cuya ruta
/// lleva el slug y no el id.
/// </summary>
public interface ITenantSlugDirectory
{
    /// <param name="normalizedSlug">El slug ya recortado y en minúsculas; un slug sin normalizar no casa con nada.</param>
    /// <returns>El id del tenant, o null si ningún tenant tiene ese slug. No mira el estado: eso lo decide quien llama.</returns>
    Task<TenantId?> FindIdBySlugAsync(string normalizedSlug, CancellationToken cancellationToken);
}
