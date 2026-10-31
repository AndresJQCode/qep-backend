using System.Globalization;
using System.Text.RegularExpressions;
using Modules.ServiceContracts.Application;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;

namespace Bootstrapper;

/// <summary>
/// Cobros recurrentes §5: el adaptador de <see cref="IServiceContractsWhatsAppOutbound"/> hacia Mensajería y el directorio de conexiones
/// <c>whatsapp-cloud</c> de Integrations (como <see cref="QuotationsWhatsAppChannelDirectory"/>). Elige la plantilla por tipo de aviso,
/// arma los valores de las variables (<c>nombre_contacto</c>, <c>servicio</c>, <c>valor</c>…) ya formateados y traduce el resultado a los <c>failure_code</c> de spec §3.4. Sólo aquí se tocan ServiceContracts,
/// Mensajería e Integrations: ServiceContracts no referencia ninguno.
/// </summary>
internal sealed partial class ServiceContractsMetaWhatsAppSender(
    IBillingTemplateOutbound outbound, IBillingTemplateReadiness readiness, IIntegrationConnections connections) : IServiceContractsWhatsAppOutbound
{
    // Meta rechaza un parámetro de más de 1024 caracteres (TemplateParameterRules.MaxValueLength).
    private const int MaxValueLength = TemplateParameterRules.MaxValueLength;

    private static readonly MessageTemplateKind[] RequiredKinds = [MessageTemplateKind.BillingReminder, MessageTemplateKind.BillingOverdue];

    public async Task<ServiceContractsSendResult> SendAsync(
        ServiceContractsWhatsAppContext context, ServiceContractsCustomerMessage message, Guid connectionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(message);
        var kind = message.Kind == ServiceContractsMessageKind.Overdue ? MessageTemplateKind.BillingOverdue : MessageTemplateKind.BillingReminder;

        // La vigente puede ser `_v2` tras un reintento: el nombre sale de la fila, no de la constante.
        if (await readiness.FindApprovedAsync(context.TenantId, connectionId, kind, cancellationToken) is not { } template)
        {
            return new ServiceContractsSendResult.Failed("template_not_approved");
        }

        var result = await outbound.SendAsync(
            new BillingTemplateSend(
                context.TenantId, connectionId, context.SentByMemberId,
                WaIdOf(message.ToAddress),
                context.CustomerId,
                CustomerName(message),
                kind,
                template,
                Values(message)),
            cancellationToken);

        return result switch
        {
            BillingTemplateSendResult.Sent => new ServiceContractsSendResult.Sent(),
            BillingTemplateSendResult.ConnectionUnavailable unavailable => new ServiceContractsSendResult.Failed($"connection_unavailable:{unavailable.Reason ?? "unknown"}"),
            BillingTemplateSendResult.Failed failed => new ServiceContractsSendResult.Failed($"send_failed:{failed.Code}"),
            _ => new ServiceContractsSendResult.Failed("send_failed:unknown"),
        };
    }

    public async Task<IReadOnlyList<ServiceContractsConnectionDto>> ListConnectionsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var listed = new List<ServiceContractsConnectionDto>();
        foreach (var summary in await connections.ListActiveAsync(tenantId, IntegrationProviders.WhatsAppCloud.Key, cancellationToken))
        {
            var resolved = (await connections.ResolveAsync(tenantId, summary.Id, cancellationToken)).Connection;
            if (resolved is null)
            {
                listed.Add(new ServiceContractsConnectionDto(summary.Id, summary.Name, null, false, "connection_unavailable"));
                continue;
            }

            var ready = true;
            foreach (var kind in RequiredKinds)
            {
                ready &= await readiness.FindApprovedAsync(tenantId, summary.Id, kind, cancellationToken) is not null;
            }

            var phone = resolved.Fields.TryGetValue(WhatsAppCloudFieldKeys.DisplayPhoneNumber, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
            listed.Add(new ServiceContractsConnectionDto(summary.Id, resolved.Name, phone, ready, ready ? null : "template_not_approved"));
        }

        return listed;
    }

    // El `wa_id` de Meta son sólo dígitos con indicativo; el teléfono del cliente es E.164 («+573001234567»).
    private static string WaIdOf(string phoneE164) => new(phoneE164.Where(char.IsAsciiDigit).ToArray());

    // Las claves son las de BillingTemplateDefinition.LegacyPositionalKeys; asesor, empresa y saludo los pone Mensajería.
    private static Dictionary<string, string> Values(ServiceContractsCustomerMessage message)
    {
        var name = CustomerName(message);
        var totals = Joined(message.TotalByCurrency.Select(total => WhatsAppTemplateValues.Total(total.Amount, total.Currency, message.Format.NumberFormat)));
        if (message.Kind == ServiceContractsMessageKind.Overdue)
        {
            return new(StringComparer.Ordinal)
            {
                ["nombre_contacto"] = name,
                ["cantidad_cobros"] = message.Lines.Count.ToString(CultureInfo.InvariantCulture),
                ["total_vencido"] = totals,
            };
        }

        var services = Joined(message.Lines.Select(line => line.ServiceName).Distinct(StringComparer.Ordinal));
        var dueDate = message.NearestDueDate.ToString(RegionalDateFormat.Pattern(message.Format.DateFormat), CultureInfo.InvariantCulture);
        return new(StringComparer.Ordinal)
        {
            ["nombre_contacto"] = name,
            ["servicio"] = services,
            ["valor"] = totals,
            ["vencimiento"] = dueDate,
        };
    }

    // Un cliente sin nombre no puede dejar la variable vacía (Meta la rechaza).
    private static string CustomerName(ServiceContractsCustomerMessage message) => OneLine(message.CustomerName) is { Length: > 0 } name ? name : "cliente";

    private static string Joined(IEnumerable<string> values) => Fit(string.Join(" y ", values.Select(OneLine).Where(value => value.Length > 0)));

    // Meta rechaza saltos de línea, tabulaciones y más de 4 espacios seguidos (QuotationTemplateOutbound.IsAcceptable): una línea, espacios simples.
    private static string OneLine(string value) => Whitespace().Replace(value, " ").Trim();

    private static string Fit(string value) => value.Length <= MaxValueLength ? value : value[..MaxValueLength];

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
