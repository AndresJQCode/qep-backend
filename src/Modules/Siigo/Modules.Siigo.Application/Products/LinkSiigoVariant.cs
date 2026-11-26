using BuildingBlocks.Application;
using FluentValidation;
using Modules.Siigo.Application.Settings;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;

namespace Modules.Siigo.Application.Products;

/// <summary><c>PUT …/products/{variantId}/link</c> (<c>siigo.settings.manage</c>): el producto de Siigo que la persona eligió para la
/// variante, con su id, código y nombre (la foto que se guarda).</summary>
public sealed record LinkSiigoVariantCommand(
    Guid TenantId, Guid ConnectionId, Guid VariantId, string? SiigoProductId, string? Code, string? Name) : ICommand<SiigoVariantLinkDto>;

/// <summary>La forma del cuerpo, con los largos de las columnas (422 <c>validation.failed</c> con el campo en camelCase).</summary>
public sealed class LinkSiigoVariantValidator : AbstractValidator<LinkSiigoVariantCommand>
{
    public LinkSiigoVariantValidator()
    {
        RuleFor(command => command.VariantId).NotEmpty().OverridePropertyName("variantId");
        RuleFor(command => command.SiigoProductId)
            .NotEmpty().MaximumLength(SiigoVariantLink.ProductIdMaxLength).OverridePropertyName("siigoProductId");
        RuleFor(command => command.Code).NotEmpty().MaximumLength(SiigoVariantLink.CodeMaxLength).OverridePropertyName("code");
        // El nombre que llegue se ignora: se toma el de Siigo.
        RuleFor(command => command.Name).MaximumLength(SiigoVariantLink.NameMaxLength).OverridePropertyName("name");
    }
}

/// <summary>
/// Spec 2026-11-18 §5.3, paso 6: vínculo manual de una fila. Un vínculo manual <b>reemplaza</b> uno automático (queda <c>Manual</c>). La
/// variante tiene que ser del tenant (422 <c>siigo.product.variant_not_found</c>). El producto <b>se valida contra Siigo</b>: se busca por
/// código y tiene que volver uno <b>activo</b> con ese id y ese código (422 <c>siigo.product.not_found_in_siigo</c>); un código viejo o
/// mal escrito daría un 4xx al facturar, y esos fallos cuentan para el bloqueo del 80 % de Siigo. El código y el nombre que se guardan
/// son los de Siigo, no los del cliente. Una conexión que no se puede usar es 409. Quién vinculó es su membresía activa. Sube la versión de la configuración y audita el paso <c>products</c>, sin valores.
/// </summary>
public sealed class LinkSiigoVariantHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ISiigoConnectionDirectory connections,
    ISiigoSettingsRepository settingsRepository,
    ISiigoVariantCatalog catalog,
    ISiigoApi api,
    IMembershipDirectory memberships,
    ISiigoUnitOfWork unitOfWork,
    ISiigoAuditRecorder auditRecorder,
    IClock clock,
    IValidator<LinkSiigoVariantCommand> validator) : ICommandHandler<LinkSiigoVariantCommand, SiigoVariantLinkDto>
{
    public async Task<SiigoVariantLinkDto> HandleAsync(LinkSiigoVariantCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await SiigoSettingsSupport.AuthorizeAsync(executionContext, tenantModules, command.TenantId, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await SiigoSettingsSupport.ConnectionAsync(connections, command.TenantId, command.ConnectionId, cancellationToken);
        var memberId = await memberships.FindActiveMembershipIdAsync(executionContext.SubjectId, command.TenantId, cancellationToken)
            ?? throw SiigoAuthorization.Denied();

        var variant = (await catalog.SearchAsync(command.TenantId, null, [command.VariantId], null, 0, 1, cancellationToken)).Items
            .SingleOrDefault()
            ?? throw new SiigoDomainException(SiigoErrorCodes.ProductVariantNotFound, "The variant does not exist in this tenant.");
        var resolution = await connections.ResolveForSetupAsync(command.TenantId, command.ConnectionId, cancellationToken);
        if (resolution.Credentials is not { } credentials)
        {
            throw new ResourceConflictException(
                SiigoErrorCodes.ConnectionUnavailable,
                $"The Siigo connection cannot be used right now ({resolution.UnavailableReason}).");
        }

        var wanted = SiigoCodeMatch.Normalize(command.Code);
        var product = (await api.SearchProductsAsync(credentials, command.Code!.Trim(), 1, cancellationToken)).Items
            .FirstOrDefault(candidate => candidate.Active
                && string.Equals(candidate.Id, command.SiigoProductId!.Trim(), StringComparison.Ordinal)
                && wanted is not null
                && SiigoCodeMatch.Normalize(candidate.Code) == wanted)
            ?? throw new SiigoDomainException(
                SiigoErrorCodes.ProductNotFoundInSiigo, "Siigo has no active product with that id and code.");
        var settings = await settingsRepository.FindOrCreateAsync(command.TenantId, command.ConnectionId, withVariantLinks: true, cancellationToken)
            ?? throw SiigoAuthorization.Denied();

        settings.LinkVariant(
            variant.VariantId, product.Code, product.Id, product.Name, SiigoLinkOrigin.Manual, memberId, clock.UtcNow);
        SiigoSettingsSupport.Audit(auditRecorder, executionContext, settings, SiigoProductLinksSupport.StepName, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return SiigoProductLinksSupport.ToRow(variant, settings.VariantLinks.Single(link => link.VariantId == variant.VariantId));
    }
}
