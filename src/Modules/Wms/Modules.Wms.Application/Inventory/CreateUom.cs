using BuildingBlocks.Application;
using FluentValidation;
using Modules.Tenancy.Application;
using Modules.Wms.Domain;
using Modules.Wms.Domain.Inventory;
using static Modules.Wms.Application.WmsAuditActions;

namespace Modules.Wms.Application;

/// <summary>Aplanado (S25): la clave del mapa <c>errors</c> es <c>Precision</c> y no <c>Request.Precision</c>.</summary>
public sealed record CreateUomCommand(Guid TenantId, string Code, string Name, int Precision) : ICommand<UomDto>
{
    public static CreateUomCommand From(Guid tenantId, CreateUomRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Un campo ausente llega null aunque el record lo declare no nulo: se vuelve vacío para que lo atrape el validador.
        return new(tenantId, request.Code ?? string.Empty, request.Name ?? string.Empty, request.Precision);
    }
}

/// <summary>El dominio lanza <see cref="ArgumentException"/> con un nombre vacío o largo, y eso sería un 500: el validador cubre
/// esos casos y la precisión, para que sean 422 <c>validation.failed</c> con el campo. Los caracteres del código (P-4) los valida
/// el dominio, que da <c>wms.uom.code_invalid</c>.</summary>
public sealed class CreateUomValidator : AbstractValidator<CreateUomCommand>
{
    public CreateUomValidator()
    {
        RuleFor(command => command.Code)
            .Must(code => !string.IsNullOrWhiteSpace(code) && code.Trim().Length <= UomCode.MaxLength)
            .WithMessage($"The code is required and has at most {UomCode.MaxLength} characters.");
        RuleFor(command => command.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= UnitOfMeasure.NameMaxLength)
            .WithMessage($"The name is required and has at most {UnitOfMeasure.NameMaxLength} characters.");
        RuleFor(command => command.Precision)
            .InclusiveBetween(UnitOfMeasure.MinPrecision, UnitOfMeasure.MaxPrecision)
            .WithMessage($"The precision must be between {UnitOfMeasure.MinPrecision} and {UnitOfMeasure.MaxPrecision}.");
    }
}

public sealed class CreateUomHandler(
    IExecutionContext executionContext,
    WmsAuthorization authorization,
    IWmsProvisioner provisioner,
    IUnitOfMeasureRepository unitsOfMeasure,
    IWmsUnitOfWork unitOfWork,
    IWmsAuditRecorder auditRecorder,
    IClock clock,
    IValidator<CreateUomCommand> validator)
    : ICommandHandler<CreateUomCommand, UomDto>
{
    public async Task<UomDto> HandleAsync(CreateUomCommand command, CancellationToken cancellationToken)
    {
        await authorization.EnsureAsync(executionContext, command.TenantId, WmsPermissions.ProfileManage, ct: cancellationToken);
        await provisioner.EnsureAsync(command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var now = clock.UtcNow;
        var uom = UnitOfMeasure.Create(command.TenantId, UomCode.Parse(command.Code), command.Name, command.Precision, isSystem: false, now);

        // La comprobación previa da el camino normal; la clave primaria (traducida en WmsUnitOfWork) cierra la carrera.
        if (await unitsOfMeasure.FindAsync(command.TenantId, uom.Code, cancellationToken) is not null)
        {
            throw new WmsDomainException(WmsErrorCodes.UomCodeTaken, "A unit of measure with this code already exists.");
        }

        unitsOfMeasure.Add(uom);

        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            WmsActorType.Human,
            UomCreated,
            Resources.Uom,
            uom.Code.Value,
            new Dictionary<string, string?>
            {
                ["code"] = uom.Code.Value,
                ["name"] = uom.Name,
                ["precision"] = uom.Precision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            now);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return UomDtoMapper.ToDto(uom);
    }
}
