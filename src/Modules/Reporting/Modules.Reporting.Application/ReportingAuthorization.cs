using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Reporting.Application;

// Autorización a nivel handler, defensa en profundidad más allá de la política del endpoint:
// el tenant activo del llamador tiene que coincidir con el de la ruta, y el permiso estar
// presente. Copia exacta del criterio de CatalogAuthorization y StorageAuthorization.
//
// Devolver 403 y no 404 es deliberado: un 404 confirmaría que el tenant de la ruta existe.
internal static class ReportingAuthorization
{
    public static void EnsureAuthorized(
        IExecutionContext executionContext,
        Guid tenantId,
        string permission)
    {
        if (executionContext.TenantId.Value != tenantId
            || !executionContext.HasPermission(permission))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot perform this reporting operation for this tenant.");
        }
    }

    /// <summary>
    /// El asesor por el que se filtra de verdad un reporte de pedidos o de cotizaciones
    /// (decisión con el dueño del producto, 2026-09-24): quien no tiene
    /// <see cref="ReportingPermissions.AllAdvisorsRead"/> sólo ve lo suyo.
    ///
    /// Con el permiso vuelve el <paramref name="requestedAdvisorId"/> tal cual, nulo incluido
    /// (nulo es "todos"). Sin él, **se ignora lo que mande el cliente** y vuelve el id de la
    /// membresía activa de quien llama: el <c>advisorId</c> es un parámetro de la URL, y confiar en
    /// él sería dejar que la pantalla decida qué datos ve cada quien. Es el id de la membresía y no
    /// el del usuario porque es lo que la cotización graba como asesor (<c>Quotation.AdvisorId</c>
    /// es un <c>MemberId</c>), igual que resuelve <c>QuotationAdvisorResolver</c> en Quotations.
    ///
    /// Sin membresía activa no hay a quién acotar, y la salida es 403: ni devolver los datos de
    /// otro ni un resultado "vacío" que en realidad no filtró nada.
    ///
    /// Tiene que correr **después** de <see cref="EnsureAuthorized"/> —así el tenant de la ruta ya
    /// es el de quien llama— y el handler tiene que usar el filtro que resulta para todo lo que
    /// consulte después: el ranking por asesor y la ventana anterior de los resúmenes incluidos.
    /// </summary>
    public static async Task<Guid?> ScopeAdvisorAsync(
        IExecutionContext executionContext,
        IMembershipDirectory membershipDirectory,
        Guid tenantId,
        Guid? requestedAdvisorId,
        CancellationToken cancellationToken)
    {
        if (executionContext.HasPermission(ReportingPermissions.AllAdvisorsRead))
        {
            return requestedAdvisorId;
        }

        var ownAdvisorId = await membershipDirectory.FindActiveMembershipIdAsync(
            executionContext.SubjectId, tenantId, cancellationToken);
        return ownAdvisorId ?? throw new RequestForbiddenException(
            "authorization.denied",
            "The subject does not have an active membership in this tenant.");
    }
}
