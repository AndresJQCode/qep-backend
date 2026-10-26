using Microsoft.EntityFrameworkCore;
using Modules.Geography.Application;
using Modules.Geography.Domain;

namespace Modules.Geography.Infrastructure.Persistence;

// Con seguimiento: lo que se lee acá se cambia y se guarda con IGeographyUnitOfWork.
internal sealed class CitySynonymRepository(GeographyDbContext dbContext) : ICitySynonymRepository
{
    public Task<CitySynonym?> FindAsync(CitySynonymId id, CancellationToken cancellationToken) =>
        dbContext.CitySynonyms.SingleOrDefaultAsync(synonym => synonym.Id == id, cancellationToken);

    public Task<CitySynonym?> FindActiveAsync(
        DepartmentId departmentId, string normalizedText, CancellationToken cancellationToken) =>
        dbContext.CitySynonyms.SingleOrDefaultAsync(
            synonym =>
                synonym.DepartmentId == departmentId &&
                synonym.NormalizedText == normalizedText &&
                synonym.DeletedAt == null,
            cancellationToken);

    public void Add(CitySynonym synonym) => dbContext.CitySynonyms.Add(synonym);
}
