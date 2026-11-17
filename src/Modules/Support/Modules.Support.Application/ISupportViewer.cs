namespace Modules.Support.Application;

/// <summary>
/// Fase 4a (plan G8): quién mira, para marcar <c>IsMe</c> en cada <see cref="SupportMemberDto"/>. El SPA no conoce su <c>memberId</c>
/// (precedente <c>AssignedToDto.IsMe</c> de Mensajería). Adaptador en Bootstrapper; scoped y sin estado.
/// </summary>
public interface ISupportViewer
{
    /// <summary>La membresía activa de quien hace la petición en el tenant, o <c>null</c> fuera de una sesión (el worker del hilo, el
    /// formulario y la consulta públicos) o sin membresía activa. Nunca lanza por falta de <c>HttpContext</c>.</summary>
    Task<Guid?> MemberIdAsync(Guid tenantId, CancellationToken cancellationToken);
}
