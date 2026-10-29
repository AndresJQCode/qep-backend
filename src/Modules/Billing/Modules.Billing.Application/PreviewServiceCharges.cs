using System.Globalization;
using BuildingBlocks.Application;
using FluentValidation;
using Modules.Billing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Billing.Application;

public sealed record PreviewServiceChargesQuery(Guid TenantId, string Cycle, DateOnly FirstDueDate) : IQuery<ChargesPreviewDto>
{
    /// <summary>
    /// Arma la consulta del query string. Una fecha ausente o mal escrita queda en <see cref="DateOnly.MinValue"/>, que el
    /// validador rechaza como <c>FirstDueDate</c>: el cliente recibe un 422 con el campo y no un 400 del binding.
    /// </summary>
    public static PreviewServiceChargesQuery From(Guid tenantId, string? cycle, string? firstDueDate) => new(
        tenantId,
        cycle ?? string.Empty,
        DateOnly.TryParseExact(firstDueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : DateOnly.MinValue);
}

public sealed class PreviewServiceChargesValidator : AbstractValidator<PreviewServiceChargesQuery>
{
    public PreviewServiceChargesValidator(IClock clock)
    {
        RuleFor(query => query.Cycle).MustBeABillingCycle();
        RuleFor(query => query.FirstDueDate).MustBeAReasonableFirstDueDate(clock);
    }
}

/// <summary>
/// Lo que <c>POST /services</c> crearía hoy con esos datos, sin escribir nada: corre <see cref="ChargeGenerator"/> sobre un
/// servicio transitorio que nunca se guarda y cuenta. Es la misma regla del alta, así que el aviso del formulario («Se van a
/// crear N cobros vencidos») no puede divergir de lo que después se crea.
/// </summary>
public sealed class PreviewServiceChargesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IClock clock,
    IValidator<PreviewServiceChargesQuery> validator)
    : IQueryHandler<PreviewServiceChargesQuery, ChargesPreviewDto>
{
    public async Task<ChargesPreviewDto> HandleAsync(PreviewServiceChargesQuery query, CancellationToken cancellationToken)
    {
        await BillingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, BillingPermissions.Read, cancellationToken);
        await validator.ValidateAndThrowAsync(query, cancellationToken);
        if (!ServiceRules.TryParseCycle(query.Cycle, out var cycle))
        {
            throw new InvalidOperationException($"Cycle '{query.Cycle}' passed validation but is not a billing cycle.");
        }

        var today = (await tenantClock.GetAsync(query.TenantId, cancellationToken)).Today;
        var now = clock.UtcNow;
        // Nombre, precio, moneda y cliente no cambian cuántos cobros nacen: valores cualquiera válidos.
        var transient = BillingService.Create(
            BillingServiceId.New(), query.TenantId, Guid.Empty, "preview", 1m, "COP", cycle, query.FirstDueDate, now);
        var charges = ChargeGenerator.Generate(transient, today, now);

        return new ChargesPreviewDto(charges.Count, charges.Count(charge => charge.IsOverdue(today)));
    }
}
