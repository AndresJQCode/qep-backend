using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §6.2: los handlers de campañas con fakes. Permiso y módulo → 403 (nunca 404 de otro tenant), el catálogo
/// valida la plantilla como en §5.2, el estado manda los 409, la versión el 412, y cada acción deja su auditoría.</summary>
public sealed class CampaignHandlersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateStoresADraftOfTheCallersMembershipWithItsAuditAndReturnsTheDetail()
    {
        var bed = new CampaignBed();

        var dto = await bed.CreateHandler().HandleAsync(bed.CreateCommand(), Ct);

        var stored = Assert.Single(bed.Repository.Campaigns.Values);
        Assert.Equal(CampaignStatus.Draft, stored.Status);
        Assert.Equal(bed.Bed.MemberId, stored.CreatedByMemberId);
        Assert.Equal("MARKETING", stored.TemplateCategory);
        Assert.Equal(1, bed.UnitOfWork.Saves);
        Assert.Equal([new CampaignAuditCall(bed.Bed.TenantId, bed.Bed.SubjectId, false, MessagingAuditActions.CampaignCreated, stored.Id)], bed.Audit.Campaigns);
        Assert.Equal(("Draft", "Promo de octubre", "Andrés", "Ventas"), (dto.Status, dto.Name, dto.CreatedBy.DisplayName, dto.Connection.Name));
        Assert.Equal(new CampaignTemplateDto("promo", "es", "MARKETING"), dto.Template);
        Assert.Equal(CampaignParameter.TextWire, dto.Parameters["2"].Source);
        Assert.Equal("A1234", dto.Parameters["2"].Value);
        Assert.Equal(CampaignParameter.CustomerNameWire, dto.Parameters["1"].Source);
    }

    [Fact]
    public async Task CreateWithoutThePermissionOrFromAnotherTenantIs403()
    {
        var bed = new CampaignBed(MessagingPermissions.ConversationManage);

        await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.CreateHandler().HandleAsync(bed.CreateCommand(), Ct));

        var elsewhere = new CampaignBed();
        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            elsewhere.CreateHandler().HandleAsync(elsewhere.CreateCommand() with { TenantId = Guid.CreateVersion7() }, Ct));
        Assert.Empty(elsewhere.Repository.Campaigns);
    }

    [Fact]
    public async Task CreateWithMessagingTurnedOffIsTheModuleError()
    {
        var bed = new CampaignBed();
        bed.Bed.TenantModules.Sets[bed.Bed.TenantId] = TenantModuleSet.Empty;

        var error = await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.CreateHandler().HandleAsync(bed.CreateCommand(), Ct));

        Assert.Equal("tenancy.module_not_enabled", error.Code);
        Assert.Empty(bed.Repository.Campaigns);
    }

    [Fact]
    public async Task CreateWithAnInvalidBodyIs422WithoutStoringAnything()
    {
        var bed = new CampaignBed();

        await Assert.ThrowsAsync<ValidationException>(() => bed.CreateHandler().HandleAsync(bed.CreateCommand(name: ""), Ct));

        Assert.Empty(bed.Repository.Campaigns);
        Assert.Equal(0, bed.UnitOfWork.Saves);
    }

    [Theory]
    [InlineData("noexiste", "es", MessagingErrorCodes.TemplateNotAvailable)]
    [InlineData("promo", "pt_BR", MessagingErrorCodes.TemplateNotAvailable)]
    public async Task CreateWithATemplateNotInTheCatalogIs422NotAvailable(string name, string language, string code)
    {
        var bed = new CampaignBed();

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            bed.CreateHandler().HandleAsync(bed.CreateCommand() with { TemplateName = name, TemplateLanguage = language }, Ct));

        Assert.Equal(code, error.Code);
        Assert.Empty(bed.Repository.Campaigns);
    }

    [Fact]
    public async Task CreateWithParameterKeysThatAreNotTheTemplatesIs422AMismatch()
    {
        var bed = new CampaignBed();
        var parameters = new Dictionary<string, CampaignParameterInput?> { ["1"] = new(CampaignParameter.TextWire, "x") };

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            bed.CreateHandler().HandleAsync(bed.CreateCommand(parameters: parameters), Ct));

        Assert.Equal(MessagingErrorCodes.TemplateParametersMismatch, error.Code);
    }

    [Fact]
    public async Task CreateWithoutAnActiveMembershipIs403()
    {
        var bed = new CampaignBed();
        bed.Membership.Active.Clear();

        await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.CreateHandler().HandleAsync(bed.CreateCommand(), Ct));
    }

    [Fact]
    public async Task GetReturnsTheDetailWithCompleteCountsAndTheAudienceWithoutWhatWasDeleted()
    {
        var bed = new CampaignBed();
        var liveTag = Guid.CreateVersion7();
        var goneTag = Guid.CreateVersion7();
        var liveCustomer = bed.Bed.Customers.Add("Laura");
        var goneCustomer = Guid.CreateVersion7();
        bed.Bed.Customers.Tags.Add(new MessagingTagRef(liveTag, "Mayoristas", "blue"));
        var campaign = bed.SeedDraft();
        campaign.Update("Promo", bed.Bed.ConnectionId, "promo", "es", "MARKETING", campaign.Parameters, [liveTag, goneTag], [liveCustomer.CustomerId, goneCustomer], null, bed.Bed.Now);
        bed.Queries.Counts = new CampaignCountsDto(10, 2, 5, 3, 1, 0, 1, 2);

        var dto = await bed.GetHandler().HandleAsync(new GetCampaignQuery(bed.Bed.TenantId, campaign.Id), Ct);

        Assert.Equal([new CampaignAudienceTagDto(liveTag, "Mayoristas", "blue")], dto.Audience.Tags);
        Assert.Equal([new CampaignAudienceCustomerDto(liveCustomer.CustomerId, "Laura")], dto.Audience.Customers);
        Assert.Equal(new CampaignCountsDto(10, 2, 5, 3, 1, 0, 1, 2), dto.Counts);
    }

    [Fact]
    public async Task ADeletedConnectionAndAMissingCreatorStillRender()
    {
        var bed = new CampaignBed();
        var campaign = bed.SeedDraft();
        bed.Bed.Connections.Names.Clear();
        bed.Membership.Active.Clear();
        var other = Campaign.Create(Guid.CreateVersion7(), bed.Bed.TenantId, bed.Bed.ConnectionId, "Otra", "promo", "es", "MARKETING", campaign.Parameters, [Guid.CreateVersion7()], [], null, Guid.CreateVersion7(), bed.Bed.Now);
        bed.Repository.Campaigns[other.Id] = other;

        var dto = await bed.GetHandler().HandleAsync(new GetCampaignQuery(bed.Bed.TenantId, other.Id), Ct);

        Assert.Equal(ConversationSummaryBuilder.DeletedConnectionName, dto.Connection.Name);
        Assert.Equal(MessageMapping.DeletedMemberName, dto.CreatedBy.DisplayName);
    }

    [Fact]
    public async Task GetOfAnotherTenantsCampaignIs403AndAnUnknownOneIs404()
    {
        var bed = new CampaignBed();
        var campaign = bed.SeedDraft();

        await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.GetHandler().HandleAsync(new GetCampaignQuery(Guid.CreateVersion7(), campaign.Id), Ct));
        var missing = await Assert.ThrowsAsync<ResourceNotFoundException>(() => bed.GetHandler().HandleAsync(new GetCampaignQuery(bed.Bed.TenantId, Guid.CreateVersion7()), Ct));
        Assert.Equal(MessagingErrorCodes.CampaignNotFound, missing.Code);
    }

    [Fact]
    public async Task ListReturnsTheNewestFirstWithItsPage()
    {
        var bed = new CampaignBed();
        var older = bed.SeedDraft();
        var newer = Campaign.Create(Guid.CreateVersion7(), bed.Bed.TenantId, bed.Bed.ConnectionId, "Nueva", "promo", "es", "MARKETING", older.Parameters, [Guid.CreateVersion7()], [], null, bed.Bed.MemberId, bed.Bed.Now.AddHours(1));
        bed.Repository.Campaigns[newer.Id] = newer;

        var page = await bed.ListHandler().HandleAsync(new ListCampaignsQuery(bed.Bed.TenantId, null, null), Ct);

        Assert.Equal([newer.Id, older.Id], page.Items.Select(item => item.Id));
        Assert.Equal((2, 1, ListCampaignsHandler.DefaultPageSize), (page.Total, page.Page, page.PageSize));
        Assert.All(page.Items, item => Assert.Equal(new CampaignCountsDto(0, 0, 0, 0, 0, 0, 0, 0), item.Counts));
    }

    [Fact]
    public async Task UpdateReplacesTheDraftAndBumpsItsVersion()
    {
        var bed = new CampaignBed();
        var campaign = bed.SeedDraft();

        var dto = await bed.UpdateHandler().HandleAsync(bed.UpdateCommand(campaign.Id, expectedVersion: 1, name: "Renombrada"), Ct);

        Assert.Equal(("Renombrada", 2L), (dto.Name, dto.Version));
        Assert.Equal(1, bed.UnitOfWork.Saves);
    }

    [Fact]
    public async Task UpdateOfANonDraftIs409NotEditableAndAStaleVersionIs412()
    {
        var bed = new CampaignBed();
        var sending = bed.SeedDraft();
        sending.Launch(bed.Bed.Now);
        var draft = bed.SeedDraft();

        var conflict = await Assert.ThrowsAsync<ResourceConflictException>(() => bed.UpdateHandler().HandleAsync(bed.UpdateCommand(sending.Id, sending.Version), Ct));
        Assert.Equal(MessagingErrorCodes.CampaignNotEditable, conflict.Code);
        await Assert.ThrowsAsync<RequestConcurrencyException>(() => bed.UpdateHandler().HandleAsync(bed.UpdateCommand(draft.Id, expectedVersion: 7), Ct));
        Assert.Equal(0, bed.UnitOfWork.Saves);
    }

    [Fact]
    public async Task DeleteRemovesADraftAndRefusesAnythingElseWith409()
    {
        var bed = new CampaignBed();
        var draft = bed.SeedDraft();
        var launched = bed.SeedDraft();
        launched.Launch(bed.Bed.Now);

        await bed.DeleteHandler().HandleAsync(new DeleteCampaignCommand(bed.Bed.TenantId, draft.Id), Ct);
        var conflict = await Assert.ThrowsAsync<ResourceConflictException>(() => bed.DeleteHandler().HandleAsync(new DeleteCampaignCommand(bed.Bed.TenantId, launched.Id), Ct));

        Assert.Equal([draft], bed.Repository.Removed);
        Assert.Equal(MessagingErrorCodes.CampaignNotEditable, conflict.Code);
        Assert.Equal(1, bed.UnitOfWork.Saves);
    }

    [Fact]
    public async Task LaunchWithoutADateStartsSendingAndAuditsIt()
    {
        var bed = new CampaignBed();
        var campaign = bed.SeedDraft();

        var dto = await bed.LaunchHandler().HandleAsync(new LaunchCampaignCommand(bed.Bed.TenantId, campaign.Id, 1), Ct);

        Assert.Equal(("Sending", 2L), (dto.Status, dto.Version));
        Assert.Equal(bed.Bed.Now, dto.StartedAt);
        Assert.Equal([new CampaignAuditCall(bed.Bed.TenantId, bed.Bed.SubjectId, false, MessagingAuditActions.CampaignLaunched, campaign.Id)], bed.Audit.Campaigns);
    }

    [Fact]
    public async Task LaunchStoresTheCategoryTheCatalogHasNowNotTheOneTheDraftWasSavedWith()
    {
        // R7: Meta puede reclasificar la plantilla entre que se guardó el borrador y el lanzamiento.
        var bed = new CampaignBed();
        var campaign = bed.SeedDraft();
        bed.Bed.Templates.Catalog.Clear();
        bed.Bed.Templates.Catalog.Add(FakeMessageTemplates.Hello("promo", "es", "UTILITY"));
        Assert.Equal("MARKETING", campaign.TemplateCategory);

        var dto = await bed.LaunchHandler().HandleAsync(new LaunchCampaignCommand(bed.Bed.TenantId, campaign.Id, 1), Ct);

        Assert.Equal("UTILITY", campaign.TemplateCategory);
        Assert.Equal("UTILITY", dto.Template.Category);
    }

    [Fact]
    public async Task LaunchWithAFutureDateSchedulesIt()
    {
        var bed = new CampaignBed();
        var campaign = bed.SeedDraft(scheduledAt: bed.Bed.Now.AddDays(2));

        var dto = await bed.LaunchHandler().HandleAsync(new LaunchCampaignCommand(bed.Bed.TenantId, campaign.Id, 1), Ct);

        Assert.Equal("Scheduled", dto.Status);
        Assert.Null(dto.StartedAt);
    }

    [Fact]
    public async Task LaunchWithADateThatAlreadyPassedIs422OnScheduledAt()
    {
        var bed = new CampaignBed();
        var campaign = bed.SeedDraft(scheduledAt: bed.Bed.Now.AddHours(1));
        bed.Clock.UtcNow = bed.Bed.Now.AddHours(2);

        var error = await Assert.ThrowsAsync<ValidationException>(() => bed.LaunchHandler().HandleAsync(new LaunchCampaignCommand(bed.Bed.TenantId, campaign.Id, 1), Ct));

        Assert.Equal(["scheduledAt"], error.Errors.Select(failure => failure.PropertyName));
        Assert.Equal(CampaignStatus.Draft, campaign.Status);
    }

    [Fact]
    public async Task LaunchChecksTheTemplateAgainstTheCatalogAgain()
    {
        var bed = new CampaignBed();
        var campaign = bed.SeedDraft();
        bed.Bed.Templates.Catalog.Clear();

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.LaunchHandler().HandleAsync(new LaunchCampaignCommand(bed.Bed.TenantId, campaign.Id, 1), Ct));

        Assert.Equal(MessagingErrorCodes.TemplateNotAvailable, error.Code);
        Assert.Equal(CampaignStatus.Draft, campaign.Status);
        Assert.Empty(bed.Audit.Campaigns);
    }

    [Fact]
    public async Task LaunchOfANonDraftIs409AndAStaleVersionIs412()
    {
        var bed = new CampaignBed();
        var sending = bed.SeedDraft();
        sending.Launch(bed.Bed.Now);
        var draft = bed.SeedDraft();

        var conflict = await Assert.ThrowsAsync<ResourceConflictException>(() => bed.LaunchHandler().HandleAsync(new LaunchCampaignCommand(bed.Bed.TenantId, sending.Id, sending.Version), Ct));
        await Assert.ThrowsAsync<RequestConcurrencyException>(() => bed.LaunchHandler().HandleAsync(new LaunchCampaignCommand(bed.Bed.TenantId, draft.Id, 9), Ct));

        Assert.Equal(MessagingErrorCodes.CampaignNotEditable, conflict.Code);
    }

    [Fact]
    public async Task CancelMovesASendingCampaignToCancelledSkippingItsPendingAndAuditsIt()
    {
        var bed = new CampaignBed();
        var campaign = bed.SeedDraft();
        campaign.Launch(bed.Bed.Now);

        var dto = await bed.CancelHandler().HandleAsync(new CancelCampaignCommand(bed.Bed.TenantId, campaign.Id, campaign.Version), Ct);

        Assert.Equal("Cancelled", dto.Status);
        Assert.Equal([campaign], bed.Repository.CancelledSaves);
        Assert.Equal([new CampaignAuditCall(bed.Bed.TenantId, bed.Bed.SubjectId, false, MessagingAuditActions.CampaignCancelled, campaign.Id)], bed.Audit.Campaigns);
    }

    [Fact]
    public async Task CancelOfADraftOrAFinishedCampaignIs409NotCancellableAndAStaleVersionIs412()
    {
        var bed = new CampaignBed();
        var draft = bed.SeedDraft();
        var sending = bed.SeedDraft();
        sending.Launch(bed.Bed.Now);

        var conflict = await Assert.ThrowsAsync<ResourceConflictException>(() => bed.CancelHandler().HandleAsync(new CancelCampaignCommand(bed.Bed.TenantId, draft.Id, 1), Ct));
        await Assert.ThrowsAsync<RequestConcurrencyException>(() => bed.CancelHandler().HandleAsync(new CancelCampaignCommand(bed.Bed.TenantId, sending.Id, 99), Ct));

        Assert.Equal(MessagingErrorCodes.CampaignNotCancellable, conflict.Code);
        Assert.Empty(bed.Repository.CancelledSaves);
    }

    [Fact]
    public async Task ARecipientPageResolvesTheirTextsAndPassesTheFilter()
    {
        var bed = new CampaignBed();
        var campaign = bed.SeedDraft();
        var conversation = Guid.CreateVersion7();
        var customer = Guid.CreateVersion7();
        bed.Queries.Recipients.Add((campaign.Id, new CampaignRecipientRow(customer, "Laura", CampaignRecipientStatus.Failed, "131049", null, bed.Bed.Now, conversation, MessageStatus.Failed)));
        bed.Queries.Recipients.Add((campaign.Id, new CampaignRecipientRow(Guid.CreateVersion7(), "Omitida", CampaignRecipientStatus.Skipped, "opted_out", null, null, null, null)));

        var failed = await bed.RecipientsHandler().HandleAsync(new ListCampaignRecipientsQuery(bed.Bed.TenantId, campaign.Id, "Failed", null, null), Ct);
        var all = await bed.RecipientsHandler().HandleAsync(new ListCampaignRecipientsQuery(bed.Bed.TenantId, campaign.Id, null, 1, 1), Ct);

        var item = Assert.Single(failed.Items);
        Assert.Equal(("Laura", "Failed", "131049", "Failed", conversation), (item.CustomerName, item.Status, item.FailureCode, item.MessageStatus, item.ConversationId));
        Assert.Equal(MessageFailureReasons.For(131049), item.FailureReason);
        Assert.Equal((2, 1, 1), (all.Total, all.Page, all.PageSize));
        Assert.Equal(CampaignFailureReasons.For("opted_out"), (await bed.RecipientsHandler().HandleAsync(new ListCampaignRecipientsQuery(bed.Bed.TenantId, campaign.Id, "Skipped", null, null), Ct)).Items[0].FailureReason);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            bed.RecipientsHandler().HandleAsync(new ListCampaignRecipientsQuery(bed.Bed.TenantId, Guid.CreateVersion7(), null, null, null), Ct));
    }

    [Fact]
    public async Task ThePreviewSubtractsOptOutsOnlyForMarketing()
    {
        var bed = new CampaignBed();
        bed.Bed.Customers.NextCount = new MessagingAudienceCount(Total: 100, Inactive: 10, OptedOut: 20, WithoutWhatsApp: 5);
        var tag = Guid.CreateVersion7();

        var marketing = await bed.PreviewHandler().HandleAsync(new PreviewAudienceQuery(bed.Bed.TenantId, [tag], [], "MARKETING"), Ct);
        var utility = await bed.PreviewHandler().HandleAsync(new PreviewAudienceQuery(bed.Bed.TenantId, [tag], [], "UTILITY"), Ct);

        Assert.Equal(new AudiencePreviewDto(100, 65, 20, 10, 5), marketing);
        Assert.Equal(new AudiencePreviewDto(100, 85, 20, 10, 5), utility);
        Assert.Equal([tag], bed.Bed.Customers.CountCalls[0].TagIds);
        Assert.Empty(bed.Bed.Customers.CountCalls[0].CustomerIds);
    }

    [Fact]
    public async Task AnEmptyPreviewIsZerosWithoutAskingCustomers()
    {
        var bed = new CampaignBed();

        var preview = await bed.PreviewHandler().HandleAsync(new PreviewAudienceQuery(bed.Bed.TenantId, [], [], "MARKETING"), Ct);

        Assert.Equal(new AudiencePreviewDto(0, 0, 0, 0, 0), preview);
        Assert.Empty(bed.Bed.Customers.CountCalls);
    }

    [Fact]
    public async Task EveryHandlerRefusesAnotherTenant()
    {
        var bed = new CampaignBed();
        var campaign = bed.SeedDraft();
        var other = Guid.CreateVersion7();

        await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.ListHandler().HandleAsync(new ListCampaignsQuery(other, null, null), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.RecipientsHandler().HandleAsync(new ListCampaignRecipientsQuery(other, campaign.Id, null, null, null), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.PreviewHandler().HandleAsync(new PreviewAudienceQuery(other, [], [], "MARKETING"), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.UpdateHandler().HandleAsync(bed.UpdateCommand(campaign.Id, 1) with { TenantId = other }, Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.DeleteHandler().HandleAsync(new DeleteCampaignCommand(other, campaign.Id), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.LaunchHandler().HandleAsync(new LaunchCampaignCommand(other, campaign.Id, 1), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.CancelHandler().HandleAsync(new CancelCampaignCommand(other, campaign.Id, 1), Ct));
        Assert.Equal(CampaignStatus.Draft, campaign.Status);
    }
}
