using Microsoft.AspNetCore.Http;
using Modules.Support.Application;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Tenancy → Support (fase 4a, plan G8): la membresía de quien hace la petición, para el <c>IsMe</c>. Sin usuario autenticado en el
/// <c>HttpContext</c> (o sin <c>HttpContext</c>: el worker del hilo) responde <c>null</c> sin tocar <see cref="IExecutionContext"/>,
/// que lanzaría; un autenticado sin sujeto (no es una persona) también responde <c>null</c>. Scoped y sin estado: no entra en <c>MultiReplicaInventory</c>.
/// </summary>
internal sealed class SupportViewer(
    IHttpContextAccessor httpContextAccessor,
    IExecutionContext executionContext,
    IMembershipDirectory memberships) : ISupportViewer
{
    public async Task<Guid?> MemberIdAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        // Revisión del bloque A: un llamador autenticado que no es una persona (una clave de API, más adelante) no trae el claim del
        // sujeto y SubjectId lanza. No es miembro de nadie, así que IsMe queda en falso en vez de tumbar la lectura.
        Guid subjectId;
        try
        {
            subjectId = executionContext.SubjectId;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return await memberships.FindActiveMembershipIdAsync(subjectId, tenantId, cancellationToken);
    }
}
