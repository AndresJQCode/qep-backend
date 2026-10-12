using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-11 §6: el agregado con tracking (la versión es el token de concurrencia, como la conversación).</summary>
internal sealed class CampaignRepository(MessagingDbContext dbContext) : ICampaignRepository
{
    public Task<Campaign?> FindAsync(Guid tenantId, Guid campaignId, CancellationToken cancellationToken) =>
        dbContext.Campaigns.SingleOrDefaultAsync(campaign => campaign.TenantId == tenantId && campaign.Id == campaignId, cancellationToken);

    public void Add(Campaign campaign) => dbContext.Campaigns.Add(campaign);

    public void Remove(Campaign campaign) => dbContext.Campaigns.Remove(campaign);

    public async Task SaveCancelledAsync(Campaign campaign, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var reason = CampaignFailureReasons.For(CampaignFailureCodes.CampaignCancelled);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new RequestConcurrencyException("concurrency.conflict", "The campaign changed while the operation was being committed.", exception);
        }

        // §6.3: sólo los Pending. Uno que el worker ya reclamó (Sending) está en vuelo y termina; la fila bloqueada hace esperar este
        // UPDATE y, al re-evaluar, ya no es Pending.
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE messaging.campaign_recipients
               SET status = 'Skipped', failure_code = {CampaignFailureCodes.CampaignCancelled}, failure_reason = {reason}, locked_until = NULL
             WHERE campaign_id = {campaign.Id} AND tenant_id = {campaign.TenantId} AND status = 'Pending'
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
