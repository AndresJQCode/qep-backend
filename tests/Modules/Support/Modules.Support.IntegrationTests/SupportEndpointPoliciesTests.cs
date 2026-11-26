using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Support.Application;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 §5.6: la política de cada ruta de ticket decide quién llega al handler, y el handler decide por ticket con la
/// puerta (<see cref="SupportTicketScope"/>). Una ruta que cambia de política sin que cambie esta tabla es una decisión de acceso que
/// nadie revisó, y una ruta nueva sin fila falla. Sólo lee los metadatos de los endpoints: no hace ninguna llamada.
/// </summary>
public sealed class SupportEndpointPoliciesTests(SharedSupportHost sharedHost) : IClassFixture<SharedSupportHost>
{
    private const string Prefix = "/api/v1/tenants/{tenantId:guid}/support";

    // (método, ruta relativa a /support) → política. Las de /tasks (Tareas 9 y 11) se suman al crearlas.
    private static readonly IReadOnlyDictionary<string, string> Expected = new Dictionary<string, string>
    {
        // Lectura que ve quien lee o gestiona.
        ["GET /tickets"] = SupportPolicies.TicketScopedView,
        ["GET /tickets/{ticketId:guid}/legal-acceptances/{kind}"] = SupportPolicies.TicketView,
        ["GET /tickets/{ticketId:guid}/replies/{replyId:guid}/document"] = SupportPolicies.TicketView,
        ["GET /tickets/{ticketId:guid}/attachments/{attachmentId:guid}/download"] = SupportPolicies.TicketView,
        ["GET /tickets/{ticketId:guid}/reply-options"] = SupportPolicies.TicketView,
        ["GET /tickets/{ticketId:guid}/messages"] = SupportPolicies.TicketView,

        // Lectura que además admite a quien usa tareas: la puerta decide por ticket.
        ["GET /tickets/{ticketId:guid}"] = SupportPolicies.TicketScopedView,
        ["GET /tickets/{ticketId:guid}/notes"] = SupportPolicies.TicketScopedView,
        ["GET /tickets/{ticketId:guid}/notes/{noteId:guid}/files/{fileId:guid}/download"] = SupportPolicies.TicketScopedView,

        // Acciones que una tarea también admite: gestionar o usar tareas, y la puerta decide por ticket.
        ["POST /tickets/{ticketId:guid}/notes"] = SupportPolicies.TicketAct,
        ["POST /tickets/{ticketId:guid}/notes/attachments"] = SupportPolicies.TicketAct,
        ["POST /tickets/{ticketId:guid}/notes/attachments/{fileId:guid}/complete"] = SupportPolicies.TicketAct,
        ["PUT /tickets/{ticketId:guid}/status"] = SupportPolicies.TicketAct,
        ["PUT /tickets/{ticketId:guid}/priority"] = SupportPolicies.TicketAct,
        ["PUT /tickets/{ticketId:guid}/assignee"] = SupportPolicies.TicketAct,
        ["POST /tickets/{ticketId:guid}/take"] = SupportPolicies.TicketAct,

        // Task 11: la fecha límite de una tarea; sobre un externo el dominio responde not_internal.
        ["PUT /tickets/{ticketId:guid}/due-date"] = SupportPolicies.TicketAct,

        // Las rutas de las tareas internas (Task 9): crear y ver quién puede recibirlas piden gestionar o usar tareas.
        ["POST /tasks"] = SupportPolicies.TicketAct,
        ["GET /tasks/assignees"] = SupportPolicies.TicketAct,

        // Sólo tienen sentido para un externo: siguen exigiendo gestionar.
        ["POST /tickets"] = SupportPermissions.Manage,
        ["POST /tickets/{ticketId:guid}/replies"] = SupportPermissions.Manage,
        ["POST /tickets/{ticketId:guid}/pin"] = SupportPermissions.Manage,
        ["PUT /tickets/{ticketId:guid}/customer"] = SupportPermissions.Manage,
        ["POST /tickets/{ticketId:guid}/extension"] = SupportPermissions.Manage,
        ["PUT /tickets/{ticketId:guid}/type"] = SupportPermissions.Manage,
        ["POST /tickets/{ticketId:guid}/messages/{messageId:guid}/pin"] = SupportPermissions.Manage,
        ["POST /tickets/{ticketId:guid}/messages/{messageId:guid}/release"] = SupportPermissions.Manage,
        ["PUT /tickets/{ticketId:guid}/follow-thread"] = SupportPermissions.Manage,
    };

    [Fact]
    public async Task EveryTicketRouteHasItsPolicy()
    {
        var factory = await sharedHost.StartTestAsync();
        var actual = new Dictionary<string, string>();
        foreach (var endpoint in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = endpoint.RoutePattern.RawText ?? string.Empty;
            if (!pattern.StartsWith($"{Prefix}/tickets", StringComparison.Ordinal)
                && !pattern.StartsWith($"{Prefix}/tasks", StringComparison.Ordinal))
            {
                continue;
            }

            var policy = endpoint.Metadata.GetMetadata<IAuthorizeData>()?.Policy ?? "<none>";
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
            foreach (var method in methods)
            {
                actual[$"{method} {pattern[Prefix.Length..]}"] = policy;
            }
        }

        // Una ruta sin fila en la tabla falla, y una fila sin ruta también.
        Assert.Equal(
            Expected.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} => {pair.Value}"),
            actual.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} => {pair.Value}"));
    }
}
