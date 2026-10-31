using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.ApiAccess.Domain;
using Modules.Audit.Domain;
using Modules.Tenancy.Application;

namespace Modules.ApiAccess.Application;

public sealed record CreateApiKeyCommand(
    Guid TenantId,
    string? Name,
    IReadOnlyList<string>? Scopes,
    IReadOnlyList<Guid>? ConnectionIds,
    DateTimeOffset? ExpiresAt) : ICommand<CreatedApiKeyDto>;

// Las claves de errors van en camelCase, que es como el formulario nombra sus campos.
public sealed class CreateApiKeyValidator : AbstractValidator<CreateApiKeyCommand>
{
    public CreateApiKeyValidator(IEnumerable<ApiScopeDefinition> definitions, IClock clock)
    {
        var known = definitions.Select(definition => definition.Key).ToHashSet(StringComparer.Ordinal);

        RuleFor(command => command.Name)
            .Must(name => !string.IsNullOrWhiteSpace(name))
            .WithMessage("Ponle un nombre a la clave.")
            .Must(name => name is null || name.Trim().Length <= ApiKey.NameMaxLength)
            .WithMessage($"El nombre de la clave no puede pasar de {ApiKey.NameMaxLength} caracteres.")
            .OverridePropertyName("name");

        RuleFor(command => command.Scopes)
            .Must(scopes => scopes is { Count: > 0 })
            .WithMessage("Elige al menos un permiso para la clave.")
            .Must(scopes => scopes is null || scopes.Distinct(StringComparer.Ordinal).Count() == scopes.Count)
            .WithMessage("No repitas permisos en la clave.")
            .Must(scopes => scopes is null || scopes.All(known.Contains))
            .WithMessage("Alguno de los permisos elegidos no existe.")
            .OverridePropertyName("scopes");

        // Una lista vacía no significa «todos los números»: eso es nulo. Se rechaza para que un
        // formulario que vació la selección no entregue acceso a todo por accidente.
        RuleFor(command => command.ConnectionIds)
            .Must(ids => ids is null || ids.Count > 0)
            .WithMessage("Elige al menos un número, o deja la clave sin restricción de números.")
            .Must(ids => ids is null || ids.All(id => id != Guid.Empty))
            .WithMessage("Alguno de los números elegidos no es válido.")
            .OverridePropertyName("connectionIds");

        RuleFor(command => command.ExpiresAt)
            .Must(expiresAt => expiresAt is null || expiresAt.Value > clock.UtcNow)
            .WithMessage("La fecha de vencimiento tiene que ser futura.")
            .OverridePropertyName("expiresAt");
    }
}

public sealed class CreateApiKeyHandler(
    IApiKeyRepository repository,
    IApiAccessUnitOfWork unitOfWork,
    IApiAccessAuditRecorder auditRecorder,
    IApiKeyConnectionDirectory connections,
    ApiScopeCatalog scopeCatalog,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IValidator<CreateApiKeyCommand> validator,
    IClock clock)
    : ICommandHandler<CreateApiKeyCommand, CreatedApiKeyDto>
{
    // Lo que se audita al crear: nombres de campo, nunca valores (y nunca el secreto).
    private static readonly string[] CreatedFields = ["connectionIds", "expiresAt", "name", "scopes"];

    public async Task<CreatedApiKeyDto> HandleAsync(
        CreateApiKeyCommand command,
        CancellationToken cancellationToken)
    {
        // Autorizar antes de validar: quien tiene el permiso en otro tenant no debe recibir el mapa de errores.
        await ApiAccessAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, ApiAccessPermissions.ApiKeyManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        var memberId = await membershipDirectory.FindActiveMembershipIdAsync(
            executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw new RequestForbiddenException(
                "authorization.denied", "The subject does not have an active membership in this tenant.");

        var now = clock.UtcNow;
        if (await repository.CountActiveAsync(command.TenantId, now, cancellationToken) >= ApiKey.MaxActivePerTenant)
        {
            throw new ApiAccessDomainException(
                ApiAccessErrorCodes.KeyLimitReached,
                $"Ya tienes {ApiKey.MaxActivePerTenant} claves activas. Revoca alguna para crear otra.");
        }

        var scopes = command.Scopes!;
        var available = (await scopeCatalog.AvailableForAsync(command.TenantId, cancellationToken))
            .ToDictionary(definition => definition.Key, StringComparer.Ordinal);
        if (scopes.Any(scope => !available.ContainsKey(scope)))
        {
            throw Invalid("scopes", "Alguno de los permisos elegidos no está disponible para tu empresa.");
        }

        IReadOnlyDictionary<Guid, string> names = new Dictionary<Guid, string>();
        if (command.ConnectionIds is not null)
        {
            if (!scopes.Any(scope => available[scope].RestrictableByConnection))
            {
                throw Invalid(
                    "connectionIds",
                    "Los permisos elegidos no se pueden limitar a ciertos números. Quita la restricción o cambia los permisos.");
            }

            names = await connections.ListAsync(command.TenantId, cancellationToken);
            if (command.ConnectionIds.Any(id => !names.ContainsKey(id)))
            {
                throw new RequestForbiddenException(
                    ExternalApiErrorCodes.ConnectionNotAllowed,
                    "One of the connections does not belong to this tenant.");
            }
        }

        var material = ApiKeyMaterial.Generate();
        var key = ApiKey.Create(
            command.TenantId,
            command.Name!,
            material,
            scopes,
            command.ConnectionIds,
            command.ExpiresAt,
            memberId,
            now);

        repository.Add(key);
        auditRecorder.Record(
            command.TenantId,
            executionContext.SubjectId,
            AuditActorType.Human,
            ApiKeyAuditActions.Created,
            key.Id.Value,
            ApiKeyAuditActions.Success,
            CreatedFields,
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new CreatedApiKeyDto(key.ToDto(scopeCatalog.All, names, now), material.Plaintext);
    }

    private static ValidationException Invalid(string property, string message) =>
        new([new ValidationFailure(property, message)]);
}
