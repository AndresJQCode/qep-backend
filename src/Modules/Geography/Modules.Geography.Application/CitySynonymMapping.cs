using Modules.Geography.Domain;

namespace Modules.Geography.Application;

/// <summary>Del agregado <see cref="CitySynonym"/> a <see cref="CitySynonymDto"/>, con los contadores de corrección y el correo de
/// quien lo creó, que viven fuera de Geography (<see cref="ICityCorrectionInsights"/>).</summary>
internal static class CitySynonymMapping
{
    public static CitySynonymDto ToDto(
        CitySynonym synonym, Department department, City city, SynonymCorrectionCounts counts, string? createdByEmail) =>
        new(
            synonym.Id.Value,
            synonym.DisplayText,
            synonym.NormalizedText,
            UnresolvedCityViews.Department(department),
            UnresolvedCityViews.City(city),
            counts.Orders,
            counts.Customers,
            counts.PendingJobs,
            counts.FailedJobs,
            new CitySynonymActorDto(synonym.CreatedBy, createdByEmail),
            synonym.CreatedAt,
            synonym.UpdatedAt,
            synonym.Version);

    /// <summary>Un solo sinónimo, después de guardarlo: lo que devuelven confirmar, corregir y reintentar.</summary>
    public static async Task<CitySynonymDto> LoadAsync(
        CitySynonym synonym,
        IDepartmentRepository departments,
        ICityRepository cities,
        ICityCorrectionInsights insights,
        CancellationToken cancellationToken)
    {
        var department = await departments.FindAsync(synonym.DepartmentId, cancellationToken)
            ?? throw new InvalidOperationException($"The department '{synonym.DepartmentId}' of synonym '{synonym.Id}' does not exist.");
        var city = await cities.FindAsync(synonym.CityId, cancellationToken)
            ?? throw new InvalidOperationException($"The city '{synonym.CityId}' of synonym '{synonym.Id}' does not exist.");
        var counts = await insights.CountCorrectionsAsync([synonym.Id], cancellationToken);
        var emails = await insights.ReadUserEmailsAsync([synonym.CreatedBy], cancellationToken);
        return ToDto(
            synonym,
            department,
            city,
            counts.GetValueOrDefault(synonym.Id.Value) ?? new SynonymCorrectionCounts(0, 0, 0, 0),
            emails.GetValueOrDefault(synonym.CreatedBy));
    }
}
