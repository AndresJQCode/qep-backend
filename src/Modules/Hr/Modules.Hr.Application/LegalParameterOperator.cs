using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Hr.Application;

// La consola de valores legales del operador (spec 2026-11-22). En cada handler la autorización de operador (Tenancy) va primero,
// antes de leer la ruta o tocar un repositorio: un 403 no depende de la entrada. Estos handlers no revisan el módulo `hr`: el tenant
// operador puede no tenerlo. Toda escritura pasa por LegalParameterWriter, la misma puerta que usará la carga por Excel.

public sealed record ListLegalParametersQuery(TenantId TenantId) : IQuery<LegalParametersDto>;

public sealed record SetLegalParameterCommand(TenantId TenantId, string Key, string EffectiveFrom, decimal Value)
    : ICommand<LegalParametersDto>;

public sealed record DeleteLegalParameterCommand(TenantId TenantId, string Key, string EffectiveFrom)
    : ICommand<LegalParametersDto>;

public sealed record SetLegalBracketsCommand(
    TenantId TenantId, string Key, string EffectiveFrom, IReadOnlyList<LegalBracketInput> Brackets)
    : ICommand<LegalParametersDto>;

public sealed record DeleteLegalBracketsCommand(TenantId TenantId, string Key, string EffectiveFrom)
    : ICommand<LegalParametersDto>;

public sealed record ListLegalParameterHistoryQuery(TenantId TenantId, int Page, int PageSize)
    : IQuery<LegalParameterHistoryPageDto>;

public sealed class ListLegalParametersHandler(
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    LegalParametersComposer composer)
    : IQueryHandler<ListLegalParametersQuery, LegalParametersDto>
{
    public Task<LegalParametersDto> HandleAsync(ListLegalParametersQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, HrOperatorPermissions.LegalParametersRead, operatorTenant);

        return composer.ComposeAsync(query.TenantId, cancellationToken);
    }
}

public sealed class SetLegalParameterHandler(
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    LegalParameterWriter writer,
    LegalParametersComposer composer)
    : ICommandHandler<SetLegalParameterCommand, LegalParametersDto>
{
    public async Task<LegalParametersDto> HandleAsync(SetLegalParameterCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, HrOperatorPermissions.LegalParametersManage, operatorTenant);

        var key = LegalParameterRoute.ParseKey(command.Key);
        var effectiveFrom = LegalParameterRoute.ParseDate(command.EffectiveFrom);
        await writer.ApplyAsync(
            command.TenantId.Value,
            [new LegalParameterEdit(key, effectiveFrom, command.Value)],
            [],
            LegalParameterChangeOrigin.Manual,
            cancellationToken);

        return await composer.ComposeAsync(command.TenantId, cancellationToken);
    }
}

public sealed class DeleteLegalParameterHandler(
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    LegalParameterWriter writer,
    LegalParametersComposer composer)
    : ICommandHandler<DeleteLegalParameterCommand, LegalParametersDto>
{
    public async Task<LegalParametersDto> HandleAsync(DeleteLegalParameterCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, HrOperatorPermissions.LegalParametersManage, operatorTenant);

        var key = LegalParameterRoute.ParseKey(command.Key);
        var effectiveFrom = LegalParameterRoute.ParseDate(command.EffectiveFrom);
        await writer.ApplyAsync(
            command.TenantId.Value,
            [new LegalParameterEdit(key, effectiveFrom, null)],
            [],
            LegalParameterChangeOrigin.Manual,
            cancellationToken);

        return await composer.ComposeAsync(command.TenantId, cancellationToken);
    }
}

public sealed class SetLegalBracketsHandler(
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    LegalParameterWriter writer,
    LegalParametersComposer composer)
    : ICommandHandler<SetLegalBracketsCommand, LegalParametersDto>
{
    public async Task<LegalParametersDto> HandleAsync(SetLegalBracketsCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, HrOperatorPermissions.LegalParametersManage, operatorTenant);

        var key = LegalParameterRoute.ParseBracketKey(command.Key);
        var effectiveFrom = LegalParameterRoute.ParseDate(command.EffectiveFrom);
        await writer.ApplyAsync(
            command.TenantId.Value,
            [],
            [new LegalBracketSetEdit(key, effectiveFrom, command.Brackets)],
            LegalParameterChangeOrigin.Manual,
            cancellationToken);

        return await composer.ComposeAsync(command.TenantId, cancellationToken);
    }
}

public sealed class DeleteLegalBracketsHandler(
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant,
    LegalParameterWriter writer,
    LegalParametersComposer composer)
    : ICommandHandler<DeleteLegalBracketsCommand, LegalParametersDto>
{
    public async Task<LegalParametersDto> HandleAsync(DeleteLegalBracketsCommand command, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, HrOperatorPermissions.LegalParametersManage, operatorTenant);

        var key = LegalParameterRoute.ParseBracketKey(command.Key);
        var effectiveFrom = LegalParameterRoute.ParseDate(command.EffectiveFrom);
        await writer.ApplyAsync(
            command.TenantId.Value,
            [],
            [new LegalBracketSetEdit(key, effectiveFrom, null)],
            LegalParameterChangeOrigin.Manual,
            cancellationToken);

        return await composer.ComposeAsync(command.TenantId, cancellationToken);
    }
}

public sealed class ListLegalParameterHistoryHandler(
    ILegalParameterRepository repository,
    IExecutionContext executionContext,
    IOperatorTenant operatorTenant)
    : IQueryHandler<ListLegalParameterHistoryQuery, LegalParameterHistoryPageDto>
{
    private const int MaxPageSize = 100;

    public async Task<LegalParameterHistoryPageDto> HandleAsync(
        ListLegalParameterHistoryQuery query, CancellationToken cancellationToken)
    {
        OperatorAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, HrOperatorPermissions.LegalParametersRead, operatorTenant);

        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

        // `(page - 1) * pageSize` es el OFFSET: con una página enorme desbordaría el int y Postgres lo rechazaría con un 500. Una
        // página más allá del último dato responde vacía, así que acotarla no esconde nada.
        var page = Math.Clamp(query.Page, 1, int.MaxValue / pageSize);
        var (items, total) = await repository.ListChangesAsync(page, pageSize, cancellationToken);

        return new LegalParameterHistoryPageDto(items.Select(ToDto).ToList(), total, page, pageSize);
    }

    // El historial no lleva columna de tipo: los nombres de los dos enums no se repiten, así que la clave lo dice.
    private static LegalParameterChangeDto ToDto(LegalParameterChange change)
    {
        var isBrackets = Enum.GetNames<LegalBracketKey>().Contains(change.Key, StringComparer.Ordinal);
        return new LegalParameterChangeDto(
            change.Id,
            isBrackets ? "Brackets" : "Parameter",
            change.Key,
            change.EffectiveFrom,
            change.Action.ToString(),
            isBrackets ? null : LegalParameterChangeJson.ReadNumber(change.OldValue),
            isBrackets ? null : LegalParameterChangeJson.ReadNumber(change.NewValue),
            isBrackets ? ToDtos(LegalParameterChangeJson.ReadBrackets(change.OldValue)) : null,
            isBrackets ? ToDtos(LegalParameterChangeJson.ReadBrackets(change.NewValue)) : null,
            change.ChangedAt,
            new LegalParameterActorDto(change.ChangedBy, change.ChangedByName),
            change.Source.ToString(),
            change.BatchId,
            change.FileName);
    }

    private static List<LegalBracketDto>? ToDtos(IReadOnlyList<LegalBracketInput>? brackets) =>
        brackets?.Select(bracket => new LegalBracketDto(bracket.LowerBound, bracket.UpperBound, bracket.Rate)).ToList();
}
