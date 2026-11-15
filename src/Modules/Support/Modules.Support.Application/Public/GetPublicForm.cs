using BuildingBlocks.Application;
using BuildingBlocks.Domain.Identification;
using Modules.Support.Domain;

namespace Modules.Support.Application;

public sealed record GetPublicFormQuery(string? TenantSlug) : IQuery<SupportPublicFormDto>;

/// <summary>
/// La configuración pública del formulario (plan F18, F19 y el contrato «Públicas»): la versión vigente del formulario, los tipos
/// activos y publicados en orden, los campos de cada variante según <see cref="SupportFormFields"/>, los tipos de documento de la tabla
/// compartida y la versión vigente de los textos legales. Un formulario publicado siempre tiene textos (plan F15); si no los tuviera, el
/// mismo 404 que cualquier tenant que no se sirve.
/// </summary>
public sealed class GetPublicFormHandler(
    SupportPublicResolver resolver,
    ISupportTicketTypeRepository ticketTypes,
    ISupportLegalTextRepository legalTexts,
    ISupportTenantBranding branding)
    : IQueryHandler<GetPublicFormQuery, SupportPublicFormDto>
{
    /// <summary>La clave de «cualquier otro país» en <see cref="SupportPublicFormDto.DocumentTypesByCountry"/>.</summary>
    public const string OtherCountries = "*";

    public async Task<SupportPublicFormDto> HandleAsync(GetPublicFormQuery query, CancellationToken cancellationToken)
    {
        var tenant = await resolver.ResolveAsync(query.TenantSlug, cancellationToken);
        var legal = await legalTexts.CurrentAsync(tenant.TenantId, cancellationToken) ?? throw SupportPublicResolver.NotFound();
        var types = (await ticketTypes.ListAsync(tenant.TenantId, cancellationToken))
            .Where(type => type.IsActive && type.IsPublished)
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
            new SupportPublicLegalDto(legal.Version, legal.AuthorizationText, legal.PolicyText, legal.PolicyUrl, legal.TermsText),
            form.Version);
    }

    private static List<SupportPublicFieldDto> Fields(SupportFormVariant variant, SupportFormVersion form) =>
        SupportFormFields.For(variant, form).Select(field => new SupportPublicFieldDto(field.Key, field.Required)).ToList();
}
