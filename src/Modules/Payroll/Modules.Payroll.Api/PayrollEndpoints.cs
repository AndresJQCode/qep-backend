using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Modules.Payroll.Api;

public static class PayrollEndpoints
{
    public static IEndpointRouteBuilder MapPayrollEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como hr y loans: la política frena a quien no tiene el permiso y el handler (PayrollAuthorization) a
        // quien lo tiene en otro tenant. Las rutas llegan con las tareas de configuración, período y capturas.
        endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/payroll")
            .WithTags("Payroll");

        return endpoints;
    }
}
