using BuildingBlocks.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §6.2: las campañas en memoria. Lo que el handler agrega, borra o cancela queda anotado.</summary>
internal sealed class FakeCampaignRepository : ICampaignRepository
{
    public Dictionary<Guid, Campaign> Campaigns { get; } = [];

    public List<Campaign> Removed { get; } = [];

    /// <summary>Cada <c>SaveCancelledAsync</c>: la cancelación y el paso de los pendientes a <c>Skipped</c> van juntos.</summary>
    public List<Campaign> CancelledSaves { get; } = [];

    public Exception? CancelFailure { get; set; }

    /// <summary>Cada <c>SaveRetriedAsync</c>: el reintento y el reencolado de los fallidos van juntos.</summary>
    public List<Campaign> RetriedSaves { get; } = [];

    public Task<Campaign?> FindAsync(Guid tenantId, Guid campaignId, CancellationToken cancellationToken) =>
        Task.FromResult(Campaigns.TryGetValue(campaignId, out var campaign) && campaign.TenantId == tenantId ? campaign : null);

    public void Add(Campaign campaign) => Campaigns[campaign.Id] = campaign;

    public void Remove(Campaign campaign)
    {
        Campaigns.Remove(campaign.Id);
        Removed.Add(campaign);
    }

    public Task SaveCancelledAsync(Campaign campaign, CancellationToken cancellationToken)
    {
        if (CancelFailure is { } failure)
        {
            throw failure;
        }

        CancelledSaves.Add(campaign);
        return Task.CompletedTask;
    }

    public Task<int> SaveRetriedAsync(Campaign campaign, DateTimeOffset now, CancellationToken cancellationToken)
    {
        RetriedSaves.Add(campaign);
        return Task.FromResult(1);
    }
}

/// <summary>Completed y Failed los escribe el worker por SQL, sin pasar por el agregado: en memoria se fijan a mano.</summary>
internal static class CampaignStates
{
    public static Campaign Finish(Campaign campaign, CampaignStatus status, string? failureCode = null)
    {
        typeof(Campaign).GetProperty(nameof(Campaign.Status))!.SetValue(campaign, status);
        typeof(Campaign).GetProperty(nameof(Campaign.CompletedAt))!.SetValue(campaign, campaign.UpdatedAt);
        typeof(Campaign).GetProperty(nameof(Campaign.FailureCode))!.SetValue(campaign, failureCode);
        typeof(Campaign).GetProperty(nameof(Campaign.FailureReason))!.SetValue(campaign, failureCode is null ? null : "La plantilla está pausada.");
        return campaign;
    }
}

internal sealed class FakeCampaignQueries(FakeCampaignRepository repository) : ICampaignQueries
{
    public CampaignCountsDto Counts { get; set; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    public List<(Guid CampaignId, CampaignRecipientRow Row)> Recipients { get; } = [];

    public (Guid CampaignId, CampaignRecipientStatus? Status, int Page, int PageSize)? LastRecipientsCall { get; private set; }

    public Task<(IReadOnlyList<CampaignRow> Items, int Total)> ListAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var all = repository.Campaigns.Values.Where(campaign => campaign.TenantId == tenantId).OrderByDescending(campaign => campaign.CreatedAt).ToList();
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).Select(campaign => RowOf(campaign, Counts)).ToList();
        return Task.FromResult<(IReadOnlyList<CampaignRow>, int)>((items, all.Count));
    }

    public Task<CampaignRow?> FindAsync(Guid tenantId, Guid campaignId, CancellationToken cancellationToken) =>
        Task.FromResult(repository.Campaigns.TryGetValue(campaignId, out var campaign) && campaign.TenantId == tenantId ? RowOf(campaign, Counts) : null);

    public Task<(IReadOnlyList<CampaignRecipientRow> Items, int Total)> ListRecipientsAsync(
        Guid tenantId, Guid campaignId, CampaignRecipientStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        LastRecipientsCall = (campaignId, status, page, pageSize);
        var all = Recipients.Where(pair => pair.CampaignId == campaignId && (status is null || pair.Row.Status == status)).Select(pair => pair.Row).ToList();
        return Task.FromResult<(IReadOnlyList<CampaignRecipientRow>, int)>((all.Skip((page - 1) * pageSize).Take(pageSize).ToList(), all.Count));
    }

    internal static CampaignRow RowOf(Campaign campaign, CampaignCountsDto counts) =>
        new(campaign.Id, campaign.ConnectionId, campaign.Name, campaign.TemplateName, campaign.TemplateLanguage, campaign.TemplateCategory,
            campaign.Parameters, campaign.AudienceTagIds, campaign.AudienceCustomerIds, campaign.Status, campaign.FailureReason, campaign.FailureCode,
            campaign.ScheduledAt, campaign.StartedAt, campaign.CompletedAt, campaign.CreatedByMemberId, campaign.CreatedAt, campaign.UpdatedAt, campaign.Version,
            campaign.RetryCount, counts);
}

internal sealed record TemplateAuditCall(Guid TenantId, Guid ActorId, string Action, Guid TemplateId, IReadOnlyCollection<string>? ChangedFields);

internal sealed record CampaignAuditCall(Guid TenantId, Guid ActorId, bool BySystem, string Action, Guid CampaignId);

internal sealed class RecordingAudit : IMessagingAuditRecorder
{
    public List<CampaignAuditCall> Campaigns { get; } = [];

    public List<TemplateAuditCall> Templates { get; } = [];

    public void Record(Guid tenantId, Guid actorId, string action, Guid conversationId, DateTimeOffset occurredAt) => throw new NotSupportedException();

    public void RecordCampaign(Guid tenantId, Guid actorId, bool bySystem, string action, Guid campaignId, DateTimeOffset occurredAt) =>
        Campaigns.Add(new CampaignAuditCall(tenantId, actorId, bySystem, action, campaignId));

    public void RecordQuickReply(Guid tenantId, Guid actorId, string action, Guid quickReplyId, DateTimeOffset occurredAt) =>
        throw new NotSupportedException();

    public void RecordResolutionReason(Guid tenantId, Guid actorId, string action, Guid reasonId, DateTimeOffset occurredAt)
    {
    }

    public void RecordTemplate(
        Guid tenantId, Guid actorId, string action, Guid templateId, DateTimeOffset occurredAt, IReadOnlyCollection<string>? changedFields = null) =>
        Templates.Add(new TemplateAuditCall(tenantId, actorId, action, templateId, changedFields));
}

internal sealed class FakeUnitOfWork : IMessagingUnitOfWork
{
    public int Saves { get; private set; }

    public Exception? Failure { get; set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            throw failure;
        }

        Saves++;
        return Task.FromResult(1);
    }
}

/// <summary>Los handlers de campañas sobre el banco de Messaging: el mismo tenant, miembro, conexión, catálogo, clientes y reloj.</summary>
internal sealed class CampaignBed
{
    public CampaignBed(params string[] permissions)
    {
        Bed = new MessagingTestBed();
        Repository = new FakeCampaignRepository();
        Queries = new FakeCampaignQueries(Repository);
        Names = new FakeMemberNames(Bed.MemberId);
        Context = new FakeExecutionContext(Bed.TenantId, Bed.SubjectId, permissions.Length == 0 ? [MessagingPermissions.CampaignManage] : permissions);
        Membership.Active[(Bed.SubjectId, Bed.TenantId)] = Bed.MemberId;
        Bed.Templates.Catalog.Add(FakeMessageTemplates.Hello("promo", "es", "MARKETING"));
        Bed.Connections.Names[Bed.ConnectionId] = "Ventas";
    }

    public MessagingTestBed Bed { get; }

    public FakeCampaignRepository Repository { get; }

    public FakeCampaignQueries Queries { get; }

    public FakeMemberNames Names { get; }

    public FakeExecutionContext Context { get; }

    public FakeMembershipDirectory Membership { get; } = new();

    public RecordingAudit Audit { get; } = new();

    public FakeUnitOfWork UnitOfWork { get; } = new();

    public FakeClock Clock => _clock ??= new FakeClock(Bed.Now);

    private FakeClock? _clock;

    public CampaignMapper Mapper => new(Bed.Connections, Names, Bed.Customers);

    public static IReadOnlyDictionary<string, CampaignParameterInput?> ValidParameters() =>
        new Dictionary<string, CampaignParameterInput?>
        {
            ["1"] = new(CampaignParameter.CustomerNameWire, null),
            ["2"] = new(CampaignParameter.TextWire, "A1234"),
        };

    public CreateCampaignCommand CreateCommand(
        string? name = "Promo de octubre", IReadOnlyDictionary<string, CampaignParameterInput?>? parameters = null,
        IReadOnlyList<Guid>? tagIds = null, IReadOnlyList<Guid>? customerIds = null, DateTimeOffset? scheduledAt = null) =>
        new(Bed.TenantId, name, Bed.ConnectionId, "promo", "es", parameters ?? ValidParameters(), tagIds ?? [Guid.CreateVersion7()], customerIds ?? [], scheduledAt);

    public UpdateCampaignCommand UpdateCommand(Guid campaignId, long? expectedVersion, string? name = "Otra") =>
        new(Bed.TenantId, campaignId, expectedVersion, name, Bed.ConnectionId, "promo", "es", ValidParameters(), [Guid.CreateVersion7()], [], null);

    public CreateCampaignHandler CreateHandler() =>
        new(Repository, Queries, UnitOfWork, Audit, Bed.Templates, Mapper, Membership, Bed.TenantModules, Context, Clock, new CreateCampaignValidator(Clock));

    public UpdateCampaignHandler UpdateHandler() =>
        new(Repository, Queries, UnitOfWork, Bed.Templates, Mapper, Bed.TenantModules, Context, Clock, new UpdateCampaignValidator(Clock));

    public DeleteCampaignHandler DeleteHandler() => new(Repository, UnitOfWork, Bed.TenantModules, Context);

    public LaunchCampaignHandler LaunchHandler() =>
        new(Repository, Queries, UnitOfWork, Audit, Bed.Templates, Mapper, Bed.TenantModules, Context, Clock, new LaunchCampaignValidator());

    public CancelCampaignHandler CancelHandler() =>
        new(Repository, Queries, Audit, Mapper, Bed.TenantModules, Context, Clock, new CancelCampaignValidator());

    public RetryCampaignHandler RetryHandler() =>
        new(Repository, Queries, Audit, Bed.Templates, Mapper, Bed.TenantModules, Context, Clock, new RetryCampaignValidator());

    public ListCampaignsHandler ListHandler() => new(Queries, Mapper, Bed.TenantModules, Context, new ListCampaignsValidator());

    public GetCampaignHandler GetHandler() => new(Queries, Mapper, Bed.TenantModules, Context);

    public ListCampaignRecipientsHandler RecipientsHandler() =>
        new(Queries, Bed.TenantModules, Context, new ListCampaignRecipientsValidator());

    public PreviewAudienceHandler PreviewHandler() => new(Bed.Customers, Bed.TenantModules, Context, new PreviewAudienceValidator());

    /// <summary>Un borrador ya guardado, con la hora de la cama.</summary>
    public Campaign SeedDraft(DateTimeOffset? scheduledAt = null, Guid? tenantId = null)
    {
        var campaign = Campaign.Create(
            Guid.CreateVersion7(), tenantId ?? Bed.TenantId, Bed.ConnectionId, "Promo", "promo", "es", "MARKETING",
            new Dictionary<string, CampaignParameter> { ["1"] = CampaignParameter.CustomerName, ["2"] = CampaignParameter.Text("A1234") },
            [Guid.CreateVersion7()], [], scheduledAt, Bed.MemberId, Bed.Now);
        Repository.Campaigns[campaign.Id] = campaign;
        return campaign;
    }
}
