using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Modules.Wms.Application;
using Modules.Wms.Domain.Control;

namespace Modules.Wms.Infrastructure.Persistence;

internal sealed class ImportPreviewStore(WmsDbContext dbContext, IOptions<WmsWorkerOptions> options) : IImportPreviewStore
{
    public TimeSpan Lifetime => TimeSpan.FromMinutes(options.Value.PreviewRetentionMinutes);

    public void Add(ImportPreview preview) => dbContext.Set<ImportPreview>().Add(preview);

    public void Remove(ImportPreview preview) => dbContext.Set<ImportPreview>().Remove(preview);

    public Task<ImportPreview?> GetAsync(Guid tenantId, Guid id, CancellationToken cancellationToken) =>
        dbContext.Set<ImportPreview>().FirstOrDefaultAsync(preview => preview.TenantId == tenantId && preview.Id == id, cancellationToken);

    public Task<int> PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync($"DELETE FROM wms.import_previews WHERE expires_at < {now}", cancellationToken);
}
