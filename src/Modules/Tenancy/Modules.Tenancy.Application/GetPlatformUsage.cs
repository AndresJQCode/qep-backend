using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Application;

public sealed record GetPlatformUsageQuery(TenantId TenantId, string? Period) : IQuery<PlatformUsageDto>;

public sealed record PlatformConnectedMemberDto(Guid MemberId, string Name);

public sealed record PlatformConversationCountsDto(int WhatsApp, int Instagram, int Total);

public sealed record PlatformUsageTenantDto(
    Guid TenantId, string Name, PlatformConversationCountsDto Conversations, IReadOnlyList<PlatformConnectedMemberDto> Connected);

/// <summary>Cuántos tenants se registraron hoy, esta semana y este mes. Viajan los tres siempre: no dependen del
/// periodo elegido, así la pantalla no tiene que pedirlos de nuevo al cambiar de pestaña.</summary>
public sealed record PlatformRegisteredCountsDto(int Day, int Week, int Month);

/// <summary><c>Period</c> devuelve el valor ya normalizado (<c>day</c>, <c>week</c> o <c>month</c>), también cuando el
/// cliente no mandó ninguno, para que la pantalla sepa qué pestaña marcar sin repetir el valor por defecto.</summary>
public sealed record PlatformUsageDto(
    PlatformRegisteredCountsDto Registered, string Period, IReadOnlyList<PlatformUsageTenantDto> Tenants);

/// <summary>Una fila por tenant. <c>CreatedAt</c> viene en la fila para que los registrados se cuenten sin
/// depender del corte que recibió la fuente.</summary>
public sealed record PlatformUsageTenantRow(
    Guid TenantId, string Name, DateTimeOffset CreatedAt, int WhatsApp, int Instagram,
    IReadOnlyList<PlatformConnectedMemberDto> Connected);

/// <summary>Puerto del reporte de uso. La implementación vive en Bootstrapper porque lee Messaging. Devuelve todos los
/// tenants sea cual sea <c>startUtc</c>, porque los registrados de hoy, la semana y el mes se cuentan sobre todas las filas.</summary>
public interface IPlatformUsageSource
{
    Task<IReadOnlyList<PlatformUsageTenantRow>> ListAsync(DateTimeOffset startUtc, CancellationToken cancellationToken);
}

public static class PlatformUsagePeriods
{
    public const string Day = "day";
    public const string Week = "week";
    public const string Month = "month";

    public static bool IsValid(string? period) => period is null or Day or Week or Month;
}

/// <summary>Un periodo fuera de <c>day</c>, <c>week</c> y <c>month</c> es <c>422 validation.failed</c> con la clave <c>Period</c>.</summary>
public sealed class GetPlatformUsageValidator : AbstractValidator<GetPlatformUsageQuery>
{
    public GetPlatformUsageValidator()
    {
        RuleFor(query => query.Period)
            .Must(PlatformUsagePeriods.IsValid)
            .WithMessage("The period must be day, week or month.");
    }
}

public sealed class GetPlatformUsageHandler(
    IPlatformUsageSource source,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    IClock clock,
    IValidator<GetPlatformUsageQuery> validator)
    : IQueryHandler<GetPlatformUsageQuery, PlatformUsageDto>
{
    private const string TimeZoneId = "America/Bogota";

    public async Task<PlatformUsageDto> HandleAsync(GetPlatformUsageQuery query, CancellationToken cancellationToken)
    {
        // La autorización va primero: un 403 no depende de la entrada.
        OperatorAuthorization.EnsureAuthorized(executionContext, query.TenantId, OperatorPermissions.UsageRead, operatorTenant);
        await validator.ValidateAndThrowAsync(query, cancellationToken);

        var period = query.Period ?? PlatformUsagePeriods.Month;
        var calendar = new TenantCalendar(clock.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId));
        var today = calendar.Today;
        var dayStart = calendar.StartOfDayUtc(today);
        // La semana empieza el lunes: DayOfWeek cuenta desde el domingo (0), así que el lunes es 1.
        var weekStart = calendar.StartOfDayUtc(today.AddDays(-(((int)today.DayOfWeek + 6) % 7)));
        var monthStart = calendar.StartOfDayUtc(new DateOnly(today.Year, today.Month, 1));
        var chosenStart = period switch
        {
            PlatformUsagePeriods.Day => dayStart,
            PlatformUsagePeriods.Week => weekStart,
            _ => monthStart,
        };

        var rows = (await source.ListAsync(chosenStart, cancellationToken))
            .Where(row => !operatorTenant.IsOperator(row.TenantId))
            .ToArray();

        var registered = new PlatformRegisteredCountsDto(
            rows.Count(row => row.CreatedAt >= dayStart),
            rows.Count(row => row.CreatedAt >= weekStart),
            rows.Count(row => row.CreatedAt >= monthStart));

        var tenants = rows
            .Select(row => new PlatformUsageTenantDto(
                row.TenantId,
                row.Name,
                new PlatformConversationCountsDto(row.WhatsApp, row.Instagram, row.WhatsApp + row.Instagram),
                row.Connected))
            .OrderByDescending(tenant => tenant.Conversations.Total)
            .ThenBy(tenant => tenant.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return new PlatformUsageDto(registered, period, tenants);
    }
}
