using BuildingBlocks.Application;
using BuildingBlocks.Domain.Pricing;
using FluentValidation;
using Modules.Audit.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record GetPriceRoundingQuery(TenantId TenantId) : IQuery<PriceRoundingSettingsDto>;

/// <summary>
/// <paramref name="Currencies"/> es nullable a propósito: un cuerpo <c>{}</c> llega como <c>null</c> y
/// el validador lo rechaza con 422 en vez de interpretarlo como «borra todas las reglas».
/// </summary>
public sealed record UpdatePriceRoundingCommand(
    TenantId TenantId,
    IReadOnlyList<CurrencyPriceRoundingInput>? Currencies) : ICommand<PriceRoundingSettingsDto>;

public sealed record CurrencyPriceRoundingInput(string Currency, int Decimals, string Mode);

/// <summary>La regla de todas las monedas del catálogo, en orden de catálogo (spec 2026-10-27).</summary>
public sealed record PriceRoundingSettingsDto(IReadOnlyList<CurrencyPriceRoundingDto> Currencies)
{
    /// <summary>Arma la respuesta desde las reglas efectivas, una por moneda del catálogo.</summary>
    public static PriceRoundingSettingsDto From(IReadOnlyDictionary<string, PriceRounding> effective) =>
        new(Application.Currencies.All
            .Select(currency =>
            {
                var rule = effective.TryGetValue(currency.Code, out var stored)
                    ? stored
                    : PriceRoundingDefaults.For(currency.Code);
                return new CurrencyPriceRoundingDto(
                    currency.Code,
                    rule.Decimals,
                    rule.Mode.ToString(),
                    currency.Decimals,
                    rule == PriceRoundingDefaults.For(currency.Code));
            })
            .ToArray());
}

/// <param name="Currency">Código del catálogo.</param>
/// <param name="Decimals">Los decimales efectivos de la regla.</param>
/// <param name="Mode"><c>Nearest</c> | <c>Up</c> | <c>Down</c>, con el nombre del enum.</param>
/// <param name="MaxDecimals">
/// Los decimales del catálogo para esta moneda, que es el tope. Viaja en cada fila porque el select de
/// decimales de la pantalla se arma por fila y no conoce el catálogo: sin este campo tendría que
/// duplicar la tabla de monedas del backend o pedir <c>/currencies</c> sólo para saber cuántas
/// opciones dibujar.
/// </param>
/// <param name="IsDefault">
/// Si la regla efectiva es la del catálogo (decimales del catálogo, al más cercano). Viaja resuelto
/// porque «sin configurar» no se deduce de <c>Decimals</c> y <c>Mode</c> sin conocer el catálogo, y
/// guardar el valor por defecto borra la fila (D3): la pantalla no puede distinguir una regla
/// guardada que coincide con el valor por defecto de una que nunca se tocó.
/// </param>
public sealed record CurrencyPriceRoundingDto(
    string Currency,
    int Decimals,
    string Mode,
    int MaxDecimals,
    bool IsDefault);

public sealed class UpdatePriceRoundingValidator : AbstractValidator<UpdatePriceRoundingCommand>
{
    public UpdatePriceRoundingValidator()
    {
        RuleFor(command => command.Currencies)
            .NotNull().WithMessage("Currencies are required.");

        // El PUT es el catálogo entero: cada moneda exactamente una vez y ninguna ajena. Una moneda
        // que falte no se interpreta como «déjala igual» porque el PUT reemplaza la lista.
        RuleFor(command => command.Currencies)
            .Must(HaveEveryCatalogueCurrencyExactlyOnce)
            .WithMessage("Currencies must list every catalogue currency exactly once.")
            .When(command => command.Currencies is not null);

        RuleForEach(command => command.Currencies).ChildRules(item =>
        {
            item.RuleFor(input => input.Decimals)
                .Must((input, decimals) => IsWithinCatalogue(input.Currency, decimals))
                .WithMessage("Decimals must be between 0 and the decimals of the currency.");
            item.RuleFor(input => input.Mode)
                .Must(mode => mode is not null
                    && Enum.GetNames<PriceRoundingMode>().Contains(mode, StringComparer.Ordinal))
                .WithMessage("Mode must be Nearest, Up or Down.");
        }).When(command => command.Currencies is not null);
    }

    private static bool HaveEveryCatalogueCurrencyExactlyOnce(IReadOnlyList<CurrencyPriceRoundingInput>? inputs)
    {
        var codes = (inputs ?? []).Select(input => input?.Currency?.Trim().ToUpperInvariant()).ToList();
        return codes.Count == Currencies.All.Count
            && codes.Distinct(StringComparer.Ordinal).Count() == codes.Count
            && Currencies.All.All(currency => codes.Contains(currency.Code));
    }

    // Una moneda ajena al catálogo ya se rechaza en Currencies; acá sólo se mide la que sí existe.
    private static bool IsWithinCatalogue(string? currency, int decimals) =>
        !Currencies.IsSupported(currency)
        || decimals >= 0 && decimals <= Currencies.Get(currency!).Decimals;
}

public sealed class GetPriceRoundingHandler(
    ITenantPriceRounding priceRounding,
    IExecutionContext executionContext)
    : IQueryHandler<GetPriceRoundingQuery, PriceRoundingSettingsDto>
{
    public async Task<PriceRoundingSettingsDto> HandleAsync(
        GetPriceRoundingQuery query,
        CancellationToken cancellationToken)
    {
        if (executionContext.TenantId != query.TenantId ||
            !executionContext.HasPermission(TenancyPermissions.SettingsRead))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot read tenant settings.");
        }

        var effective = await priceRounding.GetAllAsync(query.TenantId.Value, cancellationToken);
        return PriceRoundingSettingsDto.From(effective);
    }
}

public sealed class UpdatePriceRoundingHandler(
    ITenantPriceRoundingRepository repository,
    ITenancyUnitOfWork unitOfWork,
    IExecutionContext executionContext,
    IAuditRecorder auditRecorder,
    IClock clock,
    IValidator<UpdatePriceRoundingCommand> validator)
    : ICommandHandler<UpdatePriceRoundingCommand, PriceRoundingSettingsDto>
{
    public async Task<PriceRoundingSettingsDto> HandleAsync(
        UpdatePriceRoundingCommand command,
        CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        EnsureAuthorized(command.TenantId);

        var stored = (await repository.ListByTenantAsync(command.TenantId, cancellationToken))
            .ToDictionary(row => row.Currency, StringComparer.Ordinal);
        var effective = new Dictionary<string, PriceRounding>(StringComparer.Ordinal);
        var changedFields = new List<string>();
        var now = clock.UtcNow;

        foreach (var input in command.Currencies!)
        {
            var code = Currencies.Normalize(input.Currency);
            var mode = Enum.Parse<PriceRoundingMode>(input.Mode);
            var desired = new PriceRounding(input.Decimals, mode);
            var defaultRule = PriceRoundingDefaults.For(code);
            stored.TryGetValue(code, out var row);
            effective[code] = desired;

            if (desired == (row?.Rule ?? defaultRule))
            {
                // D11: lo que no cambia no escribe, ni siquiera `updated_at`.
                continue;
            }

            changedFields.Add(code);
            if (desired == defaultRule)
            {
                // D3: guardar el valor por defecto borra la fila, así «sin configurar» tiene una sola forma.
                repository.Remove(row!);
            }
            else if (row is null)
            {
                repository.Add(TenantPriceRounding.Create(command.TenantId, code, input.Decimals, mode, now));
            }
            else
            {
                row.Change(input.Decimals, mode, now);
            }
        }

        if (changedFields.Count > 0)
        {
            // Misma transacción que la regla: o quedan las dos o ninguna.
            auditRecorder.Record(
                command.TenantId.Value,
                executionContext.SubjectId,
                "tenancy.price_rounding.updated",
                "tenant",
                command.TenantId.ToString(),
                "success",
                changedFields,
                now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return PriceRoundingSettingsDto.From(effective);
    }

    private void EnsureAuthorized(TenantId tenantId)
    {
        if (executionContext.TenantId != tenantId ||
            !executionContext.HasPermission(TenancyPermissions.SettingsUpdate))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot update tenant settings.");
        }
    }
}
