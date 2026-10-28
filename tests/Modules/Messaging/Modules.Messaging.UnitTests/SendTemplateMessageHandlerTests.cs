using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §5.2: las mismas guardas que el texto (permiso, módulo, membresía, clientId, dueño, autoasignación,
/// Open), sin exigir la ventana; la plantilla tiene que estar en el catálogo, enviable y con las claves exactas; a <c>to</c> si hay
/// teléfono y si no a <c>recipient</c>; se guarda <c>Template</c> con su texto armado y su detalle.</summary>
public sealed class SendTemplateMessageHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> Values(string first = "Ana", string second = "A-1") =>
        new Dictionary<string, string?> { ["1"] = first, ["2"] = second };

    private static SendTemplateMessageCommand Command(MessagingTestBed bed, ConversationRow conversation, IReadOnlyDictionary<string, string?>? parameters = null, string name = "hola", string language = "es") =>
        new(bed.TenantId, conversation.Id, bed.ClientId, name, language, parameters ?? Values());

    // TemplateSend lleva una lista: el == de un record la compara por referencia, así que se comparan los campos.
    private static void AssertSend(TemplateSend actual, bool named, params (string Key, string Value)[] parameters)
    {
        Assert.Equal(("hola", "es", named), (actual.Name, actual.Language, actual.Named));
        Assert.Equal(parameters, actual.Parameters.Select(parameter => (parameter.Key, parameter.Value)));
    }

    private static MessagingTestBed BedWithHello()
    {
        var bed = new MessagingTestBed();
        bed.Templates.Catalog.Add(FakeMessageTemplates.Hello());
        return bed;
    }

    [Fact]
    public async Task ATemplateGoesWithTheWindowClosedAndKeepsTheRenderedTextAndTheTemplateDetail()
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 72, userId: "CO.1349120865530274");
        bed.Meta.NextSend = new SendResult(SendOutcome.Sent, "wamid.tpl", null, null);

        var message = await bed.SendTemplateHandler().HandleAsync(Command(bed, conversation), Ct);

        Assert.Equal(("Template", "Sent", "Outbound"), (message.Kind, message.Status, message.Direction));
        Assert.Equal("Hola Ana, tu pedido A-1 salió\n\nGracias", message.Text);
        Assert.Equal(new MessageTemplateRefDto("hola", "es"), message.Template);
        Assert.Equal(bed.ClientId, message.ClientId);
        Assert.Equal(bed.MemberId, message.SentBy!.MemberId);
        var claim = Assert.Single(bed.Outbound.Claims);
        Assert.Equal("committed-sent:wamid.tpl", claim.Outcome);
        Assert.Equal(MessageKind.Template, claim.Draft.Kind);
        Assert.Equal(message.Text, claim.Draft.Text);
        Assert.Null(claim.Draft.ReplyToMessageId);
        using var details = System.Text.Json.JsonDocument.Parse(claim.Draft.DetailsJson!);
        var template = details.RootElement.GetProperty("template");
        Assert.Equal(("hola", "es", "UTILITY"), (template.GetProperty("name").GetString(), template.GetProperty("language").GetString(), template.GetProperty("category").GetString()));
        Assert.Equal("Ana", template.GetProperty("parameters").GetProperty("1").GetString());
    }

    [Fact]
    public async Task TheTemplateGoesByPhoneEvenWhenTheConversationHasABsuid()
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: null, userId: "CO.1349120865530274");

        await bed.SendTemplateHandler().HandleAsync(Command(bed, conversation), Ct);

        var send = Assert.Single(bed.Meta.TemplateSends);
        Assert.Equal(new SendToPhone("573001234567"), send.Target);
        AssertSend(send.Template, named: false, ("1", "Ana"), ("2", "A-1"));
        Assert.Equal($"qep:{Assert.Single(bed.Outbound.Claims).MessageId}", send.CallbackData);
        Assert.Empty(bed.Meta.Sends);
    }

    [Fact]
    public async Task AConversationWithOnlyABsuidIsReachedByRecipient()
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, userId: "CO.1349120865530274", waId: null);

        await bed.SendTemplateHandler().HandleAsync(Command(bed, conversation), Ct);

        Assert.Equal(new SendToUserId("CO.1349120865530274"), Assert.Single(bed.Meta.TemplateSends).Target);
    }

    [Fact]
    public async Task ANamedTemplateSendsItsParameterNamesInTheOrderOfTheTemplate()
    {
        var bed = new MessagingTestBed();
        bed.Templates.Catalog.Add(FakeMessageTemplates.Hello() with { ParameterFormat = TemplateCatalog.Named, Body = "Hola {{first_name}}, {{order_id}}", Parameters = ["first_name", "order_id"] });
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        var parameters = new Dictionary<string, string?> { ["order_id"] = "A-1", ["first_name"] = "Ana" };

        await bed.SendTemplateHandler().HandleAsync(Command(bed, conversation, parameters), Ct);

        AssertSend(Assert.Single(bed.Meta.TemplateSends).Template, named: true, ("first_name", "Ana"), ("order_id", "A-1"));
    }

    [Theory]
    [InlineData("otra", "es")]
    [InlineData("hola", "pt_BR")]
    public async Task ATemplateThatIsNotInTheCatalogIs422WithoutClaimNorMeta(string name, string language)
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() =>
            bed.SendTemplateHandler().HandleAsync(Command(bed, conversation, name: name, language: language), Ct));

        Assert.Equal("messaging.template.not_available", error.Code);
        Assert.Empty(bed.Outbound.Claims);
        Assert.Empty(bed.Meta.TemplateSends);
        Assert.Empty(bed.Repository.AutoAssigned);
    }

    [Fact]
    public async Task ATemplateThatIsNotSendableIsNotAvailable()
    {
        var bed = new MessagingTestBed();
        bed.Templates.Catalog.Add(FakeMessageTemplates.Hello() with { Sendable = false, UnsupportedReason = "media_header" });
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendTemplateHandler().HandleAsync(Command(bed, conversation), Ct));

        Assert.Equal("messaging.template.not_available", error.Code);
    }

    [Fact]
    public async Task TheCatalogOfTheConversationsConnectionIsTheOneAsked()
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        await bed.SendTemplateHandler().HandleAsync(Command(bed, conversation), Ct);

        Assert.Equal((bed.TenantId, bed.ConnectionId, false), Assert.Single(bed.Templates.Calls));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1", "2", "3")]
    [InlineData("1", "nombre")]
    [InlineData]
    public async Task ParameterKeysThatAreNotExactlyTheTemplatesAreAMismatch(params string[] keys)
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        var parameters = keys.ToDictionary(key => key, _ => (string?)"x");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendTemplateHandler().HandleAsync(Command(bed, conversation, parameters), Ct));

        Assert.Equal("messaging.template.parameters_mismatch", error.Code);
        Assert.Empty(bed.Outbound.Claims);
        Assert.Empty(bed.Meta.TemplateSends);
    }

    [Fact]
    public async Task ARepeatedClientIdReturnsWhatWasStoredWithItsTemplateWithoutCallingMetaNorTheCatalog()
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 72);
        var details = TemplateDetailsJson.Serialize("hola", "es", "UTILITY", new Dictionary<string, string> { ["1"] = "Ana", ["2"] = "A-1" });
        bed.Outbound.Committed = new ExistingOutbound(
            Guid.CreateVersion7(), MessageStatus.Delivered, bed.Now.AddMinutes(-5), bed.MemberId, "Hola Ana, tu pedido A-1 salió", Kind: MessageKind.Template, DetailsJson: details);

        var message = await bed.SendTemplateHandler().HandleAsync(Command(bed, conversation, name: "otra"), Ct);

        Assert.Equal(bed.Outbound.Committed.Id, message.Id);
        Assert.Equal(("Template", "Delivered", "Hola Ana, tu pedido A-1 salió"), (message.Kind, message.Status, message.Text));
        Assert.Equal(new MessageTemplateRefDto("hola", "es"), message.Template);
        Assert.Empty(bed.Meta.TemplateSends);
        Assert.Empty(bed.Templates.Calls);
        Assert.Empty(bed.Outbound.Claims);
    }

    [Fact]
    public async Task AnInFlightClientIdIsResolvedByTheClaimWithoutCallingMeta()
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        var details = TemplateDetailsJson.Serialize("hola", "es", "UTILITY", new Dictionary<string, string> { ["1"] = "Ana", ["2"] = "A-1" });
        bed.Outbound.Existing = new ExistingOutbound(
            Guid.CreateVersion7(), MessageStatus.Sent, bed.Now.AddMinutes(-1), bed.MemberId, "ya salió", Kind: MessageKind.Template, DetailsJson: details);

        var message = await bed.SendTemplateHandler().HandleAsync(Command(bed, conversation), Ct);

        Assert.Equal((bed.Outbound.Existing.Id, "ya salió"), (message.Id, message.Text));
        Assert.Empty(bed.Meta.TemplateSends);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
    }

    [Fact]
    public async Task AnUnassignedConversationIsTakenBySending()
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: null);

        await bed.SendTemplateHandler().HandleAsync(Command(bed, conversation), Ct);

        Assert.Equal(conversation.Id, Assert.Single(bed.Repository.AutoAssigned));
    }

    [Fact]
    public async Task AConversationAssignedToSomeoneElseIs422WithoutClaimNorMeta()
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, assignedTo: Guid.CreateVersion7());

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendTemplateHandler().HandleAsync(Command(bed, conversation), Ct));

        Assert.Equal(MessagingErrorCodes.AssignedToOther, error.Code);
        Assert.Empty(bed.Outbound.Claims);
        Assert.Empty(bed.Meta.TemplateSends);
    }

    [Fact]
    public async Task AResolvedConversationMustBeReopenedFirst()
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, resolved: true);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendTemplateHandler().HandleAsync(Command(bed, conversation), Ct));

        Assert.Equal(MessagingErrorCodes.ConversationNotOpen, error.Code);
    }

    [Fact]
    public async Task ATemplateToABlockedContactIsRefusedBeforeTheClaim()
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 90, blocked: true);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendTemplateHandler().HandleAsync(Command(bed, conversation), Ct));

        Assert.Equal(MessagingErrorCodes.ContactBlocked, error.Code);
        Assert.Empty(bed.Outbound.Claims);
        Assert.Empty(bed.Meta.TemplateSends);
    }

    [Fact]
    public async Task AnotherTenantIs403AndNeverReachesTheCatalog()
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.SendTemplateHandler(tenantId: Guid.CreateVersion7()).HandleAsync(Command(bed, conversation), Ct));

        Assert.Empty(bed.Templates.Calls);
    }

    [Fact]
    public async Task AnInvalidValueIsAValidationErrorBeforeAnythingElse()
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.SendTemplateHandler().HandleAsync(Command(bed, conversation, new Dictionary<string, string?> { ["1"] = "a\nb", ["2"] = "ok" }), Ct));

        Assert.Equal(["parameters.1"], error.Errors.Select(failure => failure.PropertyName));
        Assert.Empty(bed.Templates.Calls);
    }

    [Fact]
    public async Task APausedConnectionRollsTheClaimBackAndIs422()
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Connections.Sender = null;

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendTemplateHandler().HandleAsync(Command(bed, conversation), Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, error.Code);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
    }

    [Theory]
    [InlineData(132001)]
    [InlineData(132015)]
    [InlineData(132016)]
    [InlineData(131049)]
    public async Task ARejectionFromMetaIsKeptAsFailedWithItsCodeAnd422(int code)
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendResult(SendOutcome.GraphError, null, code, "x");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendTemplateHandler().HandleAsync(Command(bed, conversation), Ct));

        Assert.Equal(MessagingErrorCodes.MessageRejected, error.Code);
        Assert.Equal($"committed-failed:{code}", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Empty(bed.Customers.OptedOut);
    }

    [Fact]
    public async Task A131050MarksTheCustomersMarketingOptOut()
    {
        var bed = BedWithHello();
        var customer = bed.Customers.Add();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1, customerId: customer.CustomerId);
        bed.Meta.NextSend = new SendResult(SendOutcome.GraphError, null, 131050, "x");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendTemplateHandler().HandleAsync(Command(bed, conversation), Ct));

        Assert.Equal(MessagingErrorCodes.MessageRejected, error.Code);
        Assert.Equal("committed-failed:131050", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Equal(customer.CustomerId, Assert.Single(bed.Customers.OptedOut));
    }

    [Fact]
    public async Task A131050WithoutACustomerMarksNobody()
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendResult(SendOutcome.GraphError, null, 131050, "x");

        await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendTemplateHandler().HandleAsync(Command(bed, conversation), Ct));

        Assert.Empty(bed.Customers.OptedOut);
    }

    [Fact]
    public async Task ARejectedCredentialRollsBackAndReportsTheConnection()
    {
        var bed = BedWithHello();
        var conversation = bed.OpenConversation(lastInboundHoursAgo: 1);
        bed.Meta.NextSend = new SendResult(SendOutcome.GraphError, null, 190, "x");

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.SendTemplateHandler().HandleAsync(Command(bed, conversation), Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, error.Code);
        Assert.Equal("rolled-back", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Equal("token_expired", Assert.Single(bed.Connections.Reported).FailureCode);
    }
}
