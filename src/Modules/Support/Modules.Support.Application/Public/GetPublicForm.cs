using BuildingBlocks.Application;
using BuildingBlocks.Domain.Identification;
using Microsoft.Extensions.Logging;
using Modules.Support.Domain;

namespace Modules.Support.Application;

public sealed record GetPublicFormQuery(string? TenantSlug) : IQuery<SupportPublicFormDto>;

/// <summary>
/// La configuración pública del formulario (plan F18, F19 y el contrato «Públicas»): la versión vigente del formulario, los tipos
/// activos y publicados en orden, los campos de cada variante según <see cref="SupportFormFields"/>, los tipos de documento de la tabla
/// compartida y la versión vigente de los tres documentos legales, saneados otra vez al servirlos, con la pregunta de autorización ya
/// resuelta y el texto fijo de caracterización (spec 2026-11-15 §6.3 y §9, plan L7, L8 y L17). Un formulario publicado siempre tiene
/// los tres (plan L18); si le faltara uno, el mismo 404 que cualquier tenant que no se sirve.
/// </summary>
public sealed class GetPublicFormHandler(
    SupportPublicResolver resolver,
    ISupportTicketTypeRepository ticketTypes,
    ISupportLegalDocumentRepository legalDocuments,
    ISupportHtmlSanitizer sanitizer,
    ISupportTenantBranding branding,
    ISupportOrganizationDirectory organizations,
    ILogger<GetPublicFormHandler> logger)
    : IQueryHandler<GetPublicFormQuery, SupportPublicFormDto>
{
    /// <summary>La clave de «cualquier otro país» en <see cref="SupportPublicFormDto.DocumentTypesByCountry"/>.</summary>
    public const string OtherCountries = "*";

    public async Task<SupportPublicFormDto> HandleAsync(GetPublicFormQuery query, CancellationToken cancellationToken)
    {
        var tenant = await resolver.ResolveAsync(query.TenantSlug, cancellationToken);
        // Una sola lectura de la empresa: la que se muestra en `organization` es la misma con que se resuelven las variables de los
        // documentos (spec 2026-11-16).
        var organization = await SupportPublicOrganization.OfAsync(organizations, tenant.Form, cancellationToken);
        var legal = await SupportServedLegal.ReadAsync(
            legalDocuments, sanitizer, tenant.TenantId, tenant.Name, organization, logger, cancellationToken);
        var types = (await ticketTypes.ListAsync(tenant.TenantId, cancellationToken))
            .Where(type => type is { IsActive: true, IsPublished: true, IsInternal: false })
            .OrderBy(type => type.Position)
            .Select(type => new SupportPublicTypeDto(type.Id.Value, type.Name, type.TermBusinessDays))
            .ToList();
        var form = tenant.Form;

        return new SupportPublicFormDto(
            tenant.Name,
            await branding.LogoUrlAsync(tenant.TenantId, cancellationToken),
            form.AccentColor,
            form.IntroText,
            tenant.Country,
            types,
            new SupportPublicFieldsDto(
                form.ShowDocument,
                Fields(SupportFormVariant.Natural, form),
                Fields(SupportFormVariant.Nit, form),
                Fields(SupportFormVariant.NoDocument, form)),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["CO"] = DocumentTypesByCountry.AllowedFor("CO"),
                ["ES"] = DocumentTypesByCountry.AllowedFor("ES"),
                [OtherCountries] = DocumentTypesByCountry.AllowedFor(OtherCountries),
            },
            legal.ToDto(),
            form.Version,
            new SupportPublicAttachmentsDto(
                form.AllowAttachments,
                SupportPublicAttachmentRules.MaxFiles,
                SupportPublicAttachmentRules.MaxSizeBytes,
                SupportPublicAttachmentRules.MimeTypes),
            organization);
    }

    private static List<SupportPublicFieldDto> Fields(SupportFormVariant variant, SupportFormVersion form) =>
        SupportFormFields.For(variant, form).Select(field => new SupportPublicFieldDto(field.Key, field.Required)).ToList();
}
