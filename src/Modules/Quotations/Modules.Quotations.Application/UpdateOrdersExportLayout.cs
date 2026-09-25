using BuildingBlocks.Application;
using FluentValidation;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Application;

/// <summary>Una columna tal como llega en el PUT (spec 2026-09-24): <c>Kind</c> como texto
/// (<c>"Catalog"</c> | <c>"Fixed"</c>), <c>Key</c> sólo en una del catálogo, <c>Value</c> sólo en
/// una fija. Todo nullable: es el validador el que dice qué falta y en qué índice.</summary>
public sealed record OrdersExportColumnInput(
    string? Kind,
    string? Key,
    string? Header,
    string? Value,
    bool Visible);

public sealed record UpdateOrdersExportLayoutCommand(
    Guid TenantId,
    IReadOnlyList<OrdersExportColumnInput>? Columns,
    long ExpectedVersion,
    string CorrelationId) : ICommand<OrdersExportLayoutDto>;

/// <summary>El nombre exacto del enum, como viaja en el DTO: ordinal, sin minúsculas ni el número
/// del miembro, que es lo que <c>Enum.TryParse</c> sí aceptaría.</summary>
internal static class OrdersExportColumnKinds
{
    public static bool TryParse(string? kind, out OrdersExportColumnKind parsed)
    {
        if (string.Equals(kind, nameof(OrdersExportColumnKind.Catalog), StringComparison.Ordinal))
        {
            parsed = OrdersExportColumnKind.Catalog;
            return true;
        }

        if (string.Equals(kind, nameof(OrdersExportColumnKind.Fixed), StringComparison.Ordinal))
        {
            parsed = OrdersExportColumnKind.Fixed;
            return true;
        }

        parsed = default;
        return false;
    }
}

/// <summary>
/// El dominio da el código, el validador da el campo: el 422 de FluentValidation es el único que
/// lleva el mapa <c>errors</c> (<c>ApiExceptionHandler.cs:58-65</c>), y con
/// <c>RuleForEach(...).ChildRules</c> el nombre sale como <c>Columns[i].Header</c>, que es lo que
/// la pantalla usa para marcar el input <c>i</c>. Las mismas reglas que las factorías de
/// <see cref="OrdersExportColumnSetting"/>, medidas recortadas.
///
/// Ronda de control, hallazgo P2: el tope de <c>Value</c> (<see cref="OrdersExportColumnSetting.FixedValueMaxLength"/>)
/// sólo aplica cuando <c>Kind</c> es <c>"Fixed"</c>. <see cref="UpdateOrdersExportLayoutHandler.ToSetting"/>
/// ignora <c>Value</c> en una del catálogo, así que validarlo ahí rechazaría un cuerpo por un
/// campo que ni se guarda.
/// </summary>
public sealed class UpdateOrdersExportLayoutValidator : AbstractValidator<UpdateOrdersExportLayoutCommand>
{
    public UpdateOrdersExportLayoutValidator()
    {
        RuleFor(command => command.Columns).NotNull();
        RuleForEach(command => command.Columns).ChildRules(column =>
        {
            column.RuleFor(item => item.Kind)
                .Must(kind => OrdersExportColumnKinds.TryParse(kind, out _))
                .WithMessage("'Kind' must be 'Catalog' or 'Fixed'.");
            column.RuleFor(item => item.Header)
                .Must(header => !string.IsNullOrWhiteSpace(header)
                    && header.Trim().Length <= OrdersExportColumnSetting.HeaderMaxLength)
                .WithMessage($"'Header' is required and cannot exceed {OrdersExportColumnSetting.HeaderMaxLength} characters.");
            column.RuleFor(item => item.Value)
                .Must(value => value is null || value.Trim().Length <= OrdersExportColumnSetting.FixedValueMaxLength)
                .When(item => string.Equals(item.Kind, nameof(OrdersExportColumnKind.Fixed), StringComparison.Ordinal))
                .WithMessage($"'Value' cannot exceed {OrdersExportColumnSetting.FixedValueMaxLength} characters.");
        });
        RuleFor(command => command.ExpectedVersion).GreaterThan(0);
    }
}

/// <summary>
/// Reemplaza el layout entero (spec 2026-09-24, D7).
///
/// Ronda de control, hallazgo B1: autoriza antes de validar. El resto del módulo autoriza
/// primero; con el validador delante (como traía el brief), un llamador sin permiso —o de otro
/// tenant— que además mandara un cuerpo inválido recibía 422 en vez de 403, y ese 422 confirma
/// que el cuerpo se leyó antes de comprobar quién lo mandaba.
///
/// D9: sin fila, el layout es el por defecto en versión 1, armado en memoria con
/// <see cref="OrdersExportLayout.CreateDefault"/>. Así el chequeo de versión es uno solo —"sin
/// fila y ExpectedVersion != 1" y "con fila y Version != ExpectedVersion" son la misma línea— y
/// el no-op también: un primer PUT idéntico al catálogo no crea fila ni audita. Dos primeros PUT
/// simultáneos chocan en la PK, que Infrastructure traduce al mismo 412.
///
/// Se audita por outbox (<see cref="IQuotationAuditPublisher"/>) sólo si cambió: registrar un
/// guardado que no cambió nada dejaría en la auditoría un cambio que no ocurrió.
/// </summary>
public sealed class UpdateOrdersExportLayoutHandler(
    IOrdersExportLayoutRepository repository,
    IQuotationsUnitOfWork unitOfWork,
    IQuotationAuditPublisher auditPublisher,
    IExecutionContext executionContext,
    IClock clock,
    IValidator<UpdateOrdersExportLayoutCommand> validator)
    : ICommandHandler<UpdateOrdersExportLayoutCommand, OrdersExportLayoutDto>
{
    public const string AuditAction = "quotations.orders_export_layout.updated";

    public async Task<OrdersExportLayoutDto> HandleAsync(
        UpdateOrdersExportLayoutCommand command,
        CancellationToken cancellationToken)
    {
        // B1: autorización antes que el validador (ver el comentario de la clase).
        QuotationsAuthorization.EnsureAuthorized(
            executionContext, command.TenantId, TenancyPermissions.SettingsUpdate);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var now = clock.UtcNow;
        var stored = await repository.FindAsync(command.TenantId, cancellationToken);
        var layout = stored ?? OrdersExportLayout.CreateDefault(command.TenantId, now);
        if (layout.Version != command.ExpectedVersion)
        {
            throw new RequestConcurrencyException(
                "concurrency.conflict",
                "The orders export layout changed after it was loaded.");
        }

        // El validador ya garantizó que Columns no es nula y que cada Kind es conocido.
        var columns = (command.Columns ?? []).Select(ToSetting).ToArray();
        if (!layout.Replace(columns, now))
        {
            return OrdersExportLayoutMappings.ToDto(stored, command.TenantId);
        }

        if (stored is null)
        {
            repository.Add(layout);
        }

        auditPublisher.Publish(
            command.TenantId,
            executionContext.SubjectId,
            AuditAction,
            command.TenantId.ToString(),
            "success",
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return OrdersExportLayoutMappings.ToDto(layout, command.TenantId);
    }

    // Las factorías normalizan y lanzan header_invalid / fixed_value_invalid; con el validador
    // delante no se alcanzan por la API (hallazgo 9), pero el dominio no depende de eso.
    private static OrdersExportColumnSetting ToSetting(OrdersExportColumnInput input) =>
        OrdersExportColumnKinds.TryParse(input.Kind, out var kind) && kind == OrdersExportColumnKind.Fixed
            ? OrdersExportColumnSetting.Fixed(input.Header, input.Value, input.Visible)
            : OrdersExportColumnSetting.Catalog(input.Key ?? string.Empty, input.Header, input.Visible);
}
