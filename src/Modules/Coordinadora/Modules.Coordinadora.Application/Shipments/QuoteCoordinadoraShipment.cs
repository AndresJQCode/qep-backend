using System.Text.Json;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Coordinadora.Domain;
using Modules.Tenancy.Application;

namespace Modules.Coordinadora.Application.Shipments;

/// <summary><c>POST …/orders/{orderId}/coordinadora-shipment/quote</c> (<c>coordinadora.shipment.manage</c>). El cuerpo es opcional.</summary>
public sealed record QuoteCoordinadoraShipmentCommand(Guid TenantId, Guid OrderId, CoordinadoraShipmentOverridesInput? Overrides)
    : ICommand<CoordinadoraQuoteDto>;

public sealed class QuoteCoordinadoraShipmentValidator : AbstractValidator<QuoteCoordinadoraShipmentCommand>
{
    public QuoteCoordinadoraShipmentValidator()
    {
        When(command => command.Overrides is not null, () =>
            RuleFor(command => command.Overrides!).SetValidator(new CoordinadoraShipmentOverridesValidator()));
    }
}

/// <summary>
/// Spec 2026-11-21 §5.5: <c>Cotizador.cotizar</c> con los mismos datos de la guía (mismo armador, mismos faltantes: 422 <c>not_ready</c>). No
/// escribe nada. Un rechazo de Coordinadora (credenciales, cuenta, ciudad) es 422 <c>coordinadora.carrier_rejected</c>; sin respuesta, 503.
/// </summary>
public sealed class QuoteCoordinadoraShipmentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ICoordinadoraOrderSource orders,
    ICoordinadoraConnectionDirectory connections,
    ICoordinadoraSettingsRepository settingsRepository,
    ICoordinadoraGeographyLookup geography,
    ICoordinadoraApi api,
    IValidator<QuoteCoordinadoraShipmentCommand> validator) : ICommandHandler<QuoteCoordinadoraShipmentCommand, CoordinadoraQuoteDto>
{
    public async Task<CoordinadoraQuoteDto> HandleAsync(QuoteCoordinadoraShipmentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await CoordinadoraShipmentSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var context = await CoordinadoraShipmentSupport.LoadAsync(
            orders, connections, settingsRepository, geography, command.TenantId, command.OrderId, cancellationToken);
        if (context.Connection is null)
        {
            throw new CoordinadoraShipmentNotReadyException([CoordinadoraMissing.Of(CoordinadoraMissing.ConnectionMissing)]);
        }

        var credentials = await CoordinadoraShipmentSupport.CredentialsAsync(connections, command.TenantId, context.Connection.Id, cancellationToken);
        var ready = CoordinadoraShipmentSupport.BuildOrThrow(
            context, credentials.Nit, CoordinadoraShipmentOverridesValidator.ToOverrides(command.Overrides));

        var outcome = await api.QuoteAsync(credentials, new CoordinadoraQuoteRequest(ready.QuoteJson), cancellationToken);
        return outcome switch
        {
            CoordinadoraQuoteOutcome.Quoted quoted => new CoordinadoraQuoteDto(
                quoted.FreightTotal,
                CoordinadoraPackageDto.From(ready.Package),
                ready.DeclaredValue,
                JsonSerializer.Deserialize<JsonElement>(quoted.ResultJson)),
            CoordinadoraQuoteOutcome.Rejected rejected => throw new CoordinadoraDomainException(
                CoordinadoraErrorCodes.CarrierRejected, $"Coordinadora rejected the quote ({rejected.Code}): {rejected.Message}"),
            CoordinadoraQuoteOutcome.Transient transient => throw new ServiceUnavailableException(
                CoordinadoraErrorCodes.Unavailable, $"Coordinadora did not answer the quote ({transient.Reason})."),
            _ => throw new InvalidOperationException("Unknown quote outcome."),
        };
    }
}
