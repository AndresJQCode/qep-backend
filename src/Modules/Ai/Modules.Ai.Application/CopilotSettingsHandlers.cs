using BuildingBlocks.Application;
using FluentValidation;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;

namespace Modules.Ai.Application;

public sealed record GetCopilotSettingsQuery(Guid TenantId) : IQuery<CopilotSettingsDto>;

/// <summary>Adenda D-10 del plan fase 2: el cuerpo de <c>PUT /copilot/settings</c> son sólo los campos editables de la respuesta, con
/// <c>If-Match</c> (<paramref name="Version"/>). Sin fila, la versión es 0.</summary>
public sealed record UpdateCopilotSettingsCommand(
    Guid TenantId,
    long Version,
    bool Enabled,
    string? Provider,
    string? Model,
    string? Instructions,
    long? MonthlyTokenBudget,
    int RetentionDays) : ICommand<CopilotSettingsDto>;

/// <summary>
/// Backend for frontend (spec 2026-11-17 copiloto §8): la pantalla de configuración se dibuja con esto y nada más. Viajan <b>todos</b> los
/// proveedores en <see cref="Providers"/> (si faltara uno, la pantalla tendría que saber cuáles existen) y en <see cref="Models"/> sólo los
/// que tienen conexión, con <c>configured</c>/<c>issue</c> de A2: el selector no ofrece un proveedor sin cuenta. El consumo del mes lo
/// suma el backend con el reloj del tenant: la pantalla nunca suma filas (regla BFF).
/// </summary>
public sealed record CopilotSettingsDto(
    bool Enabled,
    string? Provider,
    string? Model,
    string Instructions,
    long? MonthlyTokenBudget,
    int RetentionDays,
    CopilotUsageDto UsageThisMonth,
    IReadOnlyList<CopilotProviderDto> Providers,
    AiModelCatalogDto Models,
    long Version);

/// <param name="ByMember">D-C12, plan C3.4: el desglose por miembro llega con esa tarea. Mientras tanto viaja <c>null</c> (el frontend lo lee
/// como lista vacía), nunca una lista vacía que diría «nadie lo usó».</param>
public sealed record CopilotUsageDto(long InputTokens, long OutputTokens, IReadOnlyList<CopilotMemberUsageDto>? ByMember);

public sealed record CopilotMemberUsageDto(Guid UserId, string Name, long InputTokens, long OutputTokens);

/// <param name="Key">La clave del proveedor, que también es la de su proveedor en Integraciones (para el enlace «Conectar»).</param>
/// <param name="Configured"><c>true</c> sólo con la conexión <c>Active</c>, con la misma tabla de <see cref="ModelCatalog.IssueOf"/>.</param>
/// <param name="Status">El estado de la conexión (<c>Active</c>, <c>Paused</c>, <c>NeedsAttention</c>) o <c>null</c> sin conexión.</param>
public sealed record CopilotProviderDto(string Key, bool Configured, string? Status);

/// <summary>D-C11 y §5.1. Los nombres de propiedad son los del cuerpo: el frontend marca el campo con la clave de <c>errors</c>.</summary>
public sealed class UpdateCopilotSettingsValidator : AbstractValidator<UpdateCopilotSettingsCommand>
{
    public UpdateCopilotSettingsValidator()
    {
        RuleFor(command => command.Instructions).MaximumLength(CopilotSettings.InstructionsMaxLength);
        RuleFor(command => command.RetentionDays).InclusiveBetween(CopilotSettings.MinRetentionDays, CopilotSettings.MaxRetentionDays);
        RuleFor(command => command.MonthlyTokenBudget).GreaterThan(0).When(command => command.MonthlyTokenBudget is not null);
        RuleFor(command => command.Provider)
            .Must(provider => provider is null || AiProvider.TryParse(provider, out _)).WithMessage("Unknown AI provider.");
        RuleFor(command => command.Model).MaximumLength(AgentModelRef.IdMaxLength);
        // Proveedor y modelo van juntos; prendido exige los dos (el CHECK de la tabla).
        RuleFor(command => command.Provider)
            .NotEmpty().When(command => command.Enabled || command.Model is not null).WithMessage("Choose the provider.");
        RuleFor(command => command.Model)
            .NotEmpty().When(command => command.Enabled || command.Provider is not null).WithMessage("Choose the model.");
    }
}

internal static class CopilotSettingsSupport
{
    /// <summary>D-C11: el mes del tenant, con su huso; el tope mensual y el consumo de la pantalla cortan acá.</summary>
    public static async Task<(DateTimeOffset Start, DateTimeOffset NextStart)> MonthAsync(
        ITenantClock tenantClock, Guid tenantId, CancellationToken cancellationToken)
    {
        var calendar = await tenantClock.GetAsync(tenantId, cancellationToken);
        var first = new DateOnly(calendar.Today.Year, calendar.Today.Month, 1);
        return (calendar.StartOfDayUtc(first), calendar.StartOfDayUtc(first.AddMonths(1)));
    }

    public static async Task<CopilotSettingsDto> ToDtoAsync(
        CopilotSettings settings,
        ICopilotRepository repository,
        IAiProviderCredentials credentials,
        IAiModelCatalog catalog,
        ITenantClock tenantClock,
        CancellationToken cancellationToken)
    {
        var (monthStart, _) = await MonthAsync(tenantClock, settings.TenantId, cancellationToken);
        var usage = await repository.SumTokensAsync(settings.TenantId, monthStart, cancellationToken);
        var states = await credentials.DescribeAsync(settings.TenantId, cancellationToken);
        var models = ModelCatalog.ToDto(catalog, states);
        var connected = states.Where(state => state.Connectable && state.ConnectionId is not null).Select(state => state.Provider.Value).ToHashSet();
        return new CopilotSettingsDto(
            settings.Enabled,
            settings.Provider,
            settings.Model,
            settings.Instructions,
            settings.MonthlyTokenBudget,
            settings.RetentionDays,
            new CopilotUsageDto(usage.InputTokens, usage.OutputTokens, ByMember: null),
            [.. AiProviders.All.Select(provider =>
            {
                var state = states.FirstOrDefault(candidate => candidate.Provider == provider);
                return new CopilotProviderDto(provider.Value, ModelCatalog.IssueOf(state) is null, state?.Status);
            })],
            models with { Providers = [.. models.Providers.Where(provider => connected.Contains(provider.Provider))] },
            settings.Version);
    }

    /// <summary>El modelo elegido tiene que estar en la lista curada del proveedor y disponible.</summary>
    public static void EnsureModelAvailable(IAiModelCatalog catalog, string provider, string model)
    {
        var available = catalog.Providers
            .Where(entry => entry.Provider.Value == provider)
            .SelectMany(entry => entry.Models)
            .Any(candidate => candidate.Id == model && candidate.Available);
        if (!available)
        {
            throw new AiDomainException(
                AiErrorCodes.CopilotModelNotAvailable,
                "Ese modelo no está disponible para el copiloto.",
                new Dictionary<string, string[]>(StringComparer.Ordinal) { ["model"] = [AiErrorCodes.CopilotModelNotAvailable] });
        }
    }
}

public sealed class GetCopilotSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ICopilotRepository repository,
    IAiProviderCredentials credentials,
    IAiModelCatalog catalog,
    ITenantClock tenantClock) : IQueryHandler<GetCopilotSettingsQuery, CopilotSettingsDto>
{
    public async Task<CopilotSettingsDto> HandleAsync(GetCopilotSettingsQuery query, CancellationToken cancellationToken)
    {
        await CopilotAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.CopilotManage, cancellationToken);
        var settings = await repository.FindSettingsAsync(query.TenantId, cancellationToken) ?? CopilotSettings.Unconfigured(query.TenantId);
        return await CopilotSettingsSupport.ToDtoAsync(settings, repository, credentials, catalog, tenantClock, cancellationToken);
    }
}

public sealed class UpdateCopilotSettingsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IValidator<UpdateCopilotSettingsCommand> validator,
    ICopilotRepository repository,
    IAiProviderCredentials credentials,
    IAiModelCatalog catalog,
    IAiUnitOfWork unitOfWork,
    IClock clock,
    ITenantClock tenantClock) : ICommandHandler<UpdateCopilotSettingsCommand, CopilotSettingsDto>
{
    public async Task<CopilotSettingsDto> HandleAsync(UpdateCopilotSettingsCommand command, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await CopilotAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.CopilotManage, cancellationToken);
        var existing = await repository.FindSettingsAsync(command.TenantId, cancellationToken);
        var settings = existing ?? CopilotSettings.Unconfigured(command.TenantId);
        if (settings.Version != command.Version)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The copilot settings changed after they were loaded.");
        }

        if (command.Provider is { } provider && command.Model is { } model)
        {
            CopilotSettingsSupport.EnsureModelAvailable(catalog, provider, model);

            // C-18: prendido exige la cuenta Active del proveedor elegido; nunca cae al otro (C-20). Apagado se puede dejar elegido.
            if (command.Enabled)
            {
                var chosen = AiProvider.Parse(provider);
                var resolution = await credentials.ResolveAsync(command.TenantId, chosen, cancellationToken);
                if (resolution.Credential is null)
                {
                    throw new AiProviderNotConfiguredException(chosen);
                }
            }
        }

        settings.Update(
            command.Enabled, command.Provider, command.Model, command.Instructions ?? string.Empty, command.MonthlyTokenBudget,
            command.RetentionDays, executionContext.SubjectId, clock.UtcNow);
        if (existing is null)
        {
            repository.AddSettings(settings);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await CopilotSettingsSupport.ToDtoAsync(settings, repository, credentials, catalog, tenantClock, cancellationToken);
    }
}
