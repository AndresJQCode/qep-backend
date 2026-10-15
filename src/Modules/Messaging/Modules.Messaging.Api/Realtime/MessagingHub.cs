using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Messaging.Api.Realtime;

/// <summary>
/// Spec 2026-10-14 (Mensajería en tiempo real), decisiones 1 y 3. Sin métodos cliente→hub en v1: la conexión
/// sólo escucha. Al conectar, la conexión entra en el grupo del tenant y en el de su membresía; los dos salen de las
/// claims, nunca del cliente. La política del endpoint ya exigió <c>messaging.conversation.read</c>; acá se repite el
/// criterio de <c>MessagingAuthorization.EnsureAsync</c> con el módulo (decisión 5 del spec de Mensajería) y se exige
/// membresía activa, porque el grupo del miembro es a quien le suena (decisión 4).
/// </summary>
public sealed class MessagingHub(IMembershipDirectory memberships, ITenantModules tenantModules) : Hub
{
    // Los nombres de las claims son los de Bootstrapper.Authentication.QepClaimTypes, que Modules.Messaging.Api no
    // referencia: `qep_sub` lo pone la cookie de sesión y `sub` el stub de desarrollo (HttpExecutionContext:16-19).
    private const string QepSubjectClaim = "qep_sub";
    private const string SubjectClaim = "sub";
    private const string TenantClaim = "tenant_id";

    public static string TenantGroup(Guid tenantId) => $"tenant:{tenantId:D}";

    public static string MemberGroup(Guid tenantId, Guid memberId) => $"member:{tenantId:D}:{memberId:D}";

    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        var subject = user?.FindFirstValue(QepSubjectClaim) ?? user?.FindFirstValue(SubjectClaim);
        if (!Guid.TryParse(subject, out var subjectId) || !Guid.TryParse(user?.FindFirstValue(TenantClaim), out var tenantId))
        {
            Context.Abort();
            return;
        }

        // Sin fila del tenant (stub con tenant simulado) el módulo no se puede negar; con fila, igual que el handler.
        var modules = await tenantModules.FindAsync(tenantId, Context.ConnectionAborted);
        if (modules is not null && !modules.IsEnabled(TenantModuleKeys.Messaging))
        {
            Context.Abort();
            return;
        }

        var memberId = await memberships.FindActiveMembershipIdAsync(subjectId, tenantId, Context.ConnectionAborted);
        if (memberId is null)
        {
            Context.Abort();
            return;
        }

        // El del miembro primero: quien ya recibe algo del grupo del tenant ya está también en el suyo, y un notify que
        // llega justo al conectar no se pierde por el orden.
        await Groups.AddToGroupAsync(Context.ConnectionId, MemberGroup(tenantId, memberId.Value), Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, TenantGroup(tenantId), Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }
}
