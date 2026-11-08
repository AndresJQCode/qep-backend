using Modules.Companies.Domain;

namespace Modules.Companies.Application;

/// <summary>
/// Ventana temporal para el frontend viejo (spec §9): un cuerpo sin país sigue siendo válido y
/// se resuelve a lo que ese cliente siempre quiso decir. Quitar esta clase, junto con su uso en
/// los handlers, es un seguimiento aparte cuando el frontend nuevo esté desplegado.
/// </summary>
public static class CompanyLegacyWrite
{
    /// <summary>
    /// Sin país: Colombia y NIT. Con país, se recorta y se pasa a mayúsculas aquí, una sola vez:
    /// validador, handler y dominio comparan después contra el mismo valor ("co" y " CO" son
    /// Colombia para los tres).
    /// </summary>
    public static CreateCompanyCommand Resolve(CreateCompanyCommand command) =>
        command.Country is not null
            ? command with { Country = Normalize(command.Country) }
            : command with
            {
                Country = Company.ColombiaCountryCode,
                DocumentType = command.DocumentType ?? CompanyDocumentTypes.ToWire(CompanyDocumentType.Nit)
            };

    /// <summary>
    /// Sin país: el que ya tiene la empresa guardada, y su tipo de documento si tampoco llega.
    /// Así una empresa española editada por el cliente viejo no se evalúa como colombiana.
    /// </summary>
    public static UpdateCompanyCommand Resolve(UpdateCompanyCommand command, Company stored) =>
        command.Country is not null
            ? command with { Country = Normalize(command.Country) }
            : command with
            {
                Country = stored.Country,
                DocumentType = command.DocumentType ?? stored.DocumentType.ToWire()
            };

    private static string Normalize(string country) => country.Trim().ToUpperInvariant();
}
