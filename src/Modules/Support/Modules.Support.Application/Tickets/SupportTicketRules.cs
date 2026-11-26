using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

/// <summary>Las reglas de un ticket que comparten el alta y las acciones, juzgadas antes de tocar el agregado: así un 422 nunca deja
/// un radicado gastado ni un evento a medias.</summary>
internal static class SupportTicketRules
{
    /// <summary>Plan D6: un ticket necesita cliente o solicitante con nombre.</summary>
    public static void EnsureRequester(Guid? customerId, TicketRequester? requester)
    {
        if (customerId is null && requester is not { HasName: true })
        {
            throw new SupportDomainException(
                SupportErrorCodes.RequesterRequired, "A ticket needs a linked customer or a requester with a name.");
        }
    }

    /// <summary>Un tipo del tenant y activo; uno de otro tenant llega como «no existe», también 422 porque el tipo es un campo del
    /// cuerpo y no el recurso de la ruta.</summary>
    public static async Task<SupportTicketType> EnsureTypeAsync(
        ISupportTicketTypeRepository types, Guid tenantId, Guid typeId, CancellationToken cancellationToken)
    {
        var type = await types.FindAsync(tenantId, new SupportTicketTypeId(typeId), cancellationToken);
        // El tipo interno sólo lo toma una tarea por POST /tasks: ninguna otra entrada lo ofrece (spec 2026-11-22, D11).
        if (type is { IsInternal: true })
        {
            throw new SupportDomainException(SupportErrorCodes.InternalTypeLocked, "The internal ticket type only takes tasks.");
        }

        if (type is not { IsActive: true })
        {
            throw new SupportDomainException(SupportErrorCodes.TicketTypeUnavailable, "The ticket type does not exist or is inactive.");
        }

        return type;
    }

    /// <summary>
    /// Plan D14: vincular un cliente exige el módulo <c>customers</c> (403 <c>tenancy.module_not_enabled</c>) y un cliente activo del
    /// tenant (422 <c>customer_not_found</c> / <c>customer_inactive</c>; un fusionado está inactivo). La existencia se vuelve a
    /// comprobar al guardar, con el candado del cliente (<c>CustomerReferenceWrites</c>): un borrado que gane la carrera sale como
    /// <c>customer_not_found</c>. Devuelve el cliente: el alta toma de ahí el contacto de respaldo.
    /// </summary>
    public static async Task<SupportCustomerContact> EnsureCustomerAsync(
        ITenantModules tenantModules, ISupportCustomerLookup customers, Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        await TenantModuleGuard.EnsureEnabledAsync(tenantModules, tenantId, TenantModuleKeys.Customers, cancellationToken);
        var customer = await customers.FindAsync(tenantId, customerId, cancellationToken)
            ?? throw new SupportDomainException(SupportErrorCodes.CustomerNotFound, $"Customer '{customerId}' was not found.");
        if (!customer.IsActive)
        {
            throw new SupportDomainException(SupportErrorCodes.CustomerInactive, "An inactive customer cannot be linked to a ticket.");
        }

        return customer;
    }

    /// <summary>El responsable es una membresía activa con <c>support.ticket.manage</c>: el mismo conjunto que ofrece
    /// <c>GET /assignees</c>.</summary>
    public static async Task EnsureAssigneeAsync(
        ISupportMemberDirectory members, Guid tenantId, Guid? assigneeMemberId, CancellationToken cancellationToken)
    {
        if (assigneeMemberId is not { } id)
        {
            return;
        }

        var found = await members.FindAsync(tenantId, [id], cancellationToken);
        if (found.Count == 0 || !found[0].IsActive || !found[0].CanManage)
        {
            throw new SupportDomainException(
                SupportErrorCodes.AssigneeInvalid, "The assignee must be an active member who can manage support tickets.");
        }
    }

    /// <summary>Spec 2026-11-22: el responsable de una tarea es una membresía activa con <c>support.task.use</c> o
    /// <c>support.ticket.manage</c>: el mismo conjunto que ofrece <c>GET /tasks/assignees</c>. Un externo sigue con
    /// <see cref="EnsureAssigneeAsync"/>.</summary>
    public static async Task EnsureTaskAssigneeAsync(
        ISupportMemberDirectory members, Guid tenantId, Guid assigneeMemberId, CancellationToken cancellationToken)
    {
        var found = await members.FindAsync(tenantId, [assigneeMemberId], cancellationToken);
        if (found.Count == 0 || !found[0].IsActive || !(found[0].CanUseTasks || found[0].CanManage))
        {
            throw new SupportDomainException(
                SupportErrorCodes.AssigneeInvalid, "The assignee must be an active member who can use internal tasks.");
        }
    }

    /// <summary>
    /// Plan D8 y spec §8.3, para un ticket manual: no hay hilo abierto, así que <c>Thread</c> no está disponible; <c>Email</c> exige
    /// el correo de la foto del solicitante y <c>WhatsAppTemplate</c> su celular. Con cliente vinculado, el alta ya copió en la foto
    /// el correo del cliente que el solicitante dejó vacío y su teléfono en el fijo, nunca en el celular
    /// (<see cref="TicketRequesterNormalizer.WithCustomerContact"/>, owner 2026-11-14 y P3 2026-11-15), así que acá basta mirar la foto:
    /// <c>WhatsAppTemplate</c> sale sólo con el celular que trajo el solicitante.
    /// </summary>
    public static void EnsureReplyChannel(SupportReplyChannel channel, TicketRequester? requester)
    {
        var available = channel switch
        {
            SupportReplyChannel.Thread => false,
            SupportReplyChannel.Email => !string.IsNullOrWhiteSpace(requester?.Email),
            SupportReplyChannel.WhatsAppTemplate => !string.IsNullOrWhiteSpace(requester?.MobilePhone),
            _ => false,
        };
        if (!available)
        {
            throw new SupportDomainException(
                SupportErrorCodes.ReplyChannelUnavailable, $"The reply channel '{channel}' is not available for this ticket.");
        }
    }

    /// <summary>Sólo el nombre exacto del contrato: <c>Enum.TryParse</c> aceptaría «0» o minúsculas, que el cable nunca debe
    /// producir.</summary>
    public static bool TryParseExact<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }
}
