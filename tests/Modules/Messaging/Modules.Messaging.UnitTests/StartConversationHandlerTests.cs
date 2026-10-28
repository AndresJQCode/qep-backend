using BuildingBlocks.Application;
using FluentValidation;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §5.2 y decisión 9: el cliente existe y está activo (404) y se le puede escribir (422); la conexión está
/// Active; la plantilla se valida antes de abrir nada; si ya hay conversación del cliente en la conexión se envía ahí (200) y si
/// no se crea asignada a quien llama (201).</summary>
public sealed class StartConversationHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MessagingTestBed Bed()
    {
        var bed = new MessagingTestBed();
        bed.Templates.Catalog.Add(FakeMessageTemplates.Hello());
        return bed;
    }

    private static StartConversationCommand Command(MessagingTestBed bed, Guid? customerId, string name = "hola", Guid? connectionId = null) =>
        new(bed.TenantId, bed.ClientId, connectionId ?? bed.ConnectionId, customerId, name, "es", new Dictionary<string, string?> { ["1"] = "Ana", ["2"] = "A-1" });

    [Fact]
    public async Task ACustomerWithoutAConversationGetsOneAssignedToTheCallerAndTheTemplateGoesByPhone()
    {
        var bed = Bed();
        var customer = bed.Customers.Add(userId: "CO.1349120865530274");

        var result = await bed.StartHandler().HandleAsync(Command(bed, customer.CustomerId), Ct);

        Assert.True(result.Created);
        var call = Assert.Single(bed.Opener.Calls);
        Assert.Equal((bed.ConnectionId, bed.MemberId, bed.SubjectId), (call.ConnectionId, call.AssignedMemberId, call.ActorUserId));
        Assert.Equal(customer, call.Contact);
        var opened = Assert.Single(bed.Conversations.Rows);
        Assert.Equal(opened.Id, result.Body.Conversation.Id);
        Assert.Equal(("Open", bed.MemberId), (result.Body.Conversation.Status, result.Body.Conversation.AssignedTo!.MemberId));
        Assert.Equal(customer.CustomerId, result.Body.Conversation.Customer!.Id);
        Assert.Equal(("Template", "Sent"), (result.Body.Message.Kind, result.Body.Message.Status));
        Assert.Equal("Hola Ana, tu pedido A-1 salió\n\nGracias", result.Body.Message.Text);
        Assert.Equal(new SendToPhone("573001234567"), Assert.Single(bed.Meta.TemplateSends).Target);
        Assert.Equal("committed-sent:wamid.default", Assert.Single(bed.Outbound.Claims).Outcome);
        Assert.Empty(bed.Repository.AutoAssigned);
    }

    [Fact]
    public async Task ACustomerWithOnlyABsuidIsReachedByRecipient()
    {
        var bed = Bed();
        var customer = bed.Customers.Add(phone: null, userId: "CO.1349120865530274");

        await bed.StartHandler().HandleAsync(Command(bed, customer.CustomerId), Ct);

        Assert.Equal(new SendToUserId("CO.1349120865530274"), Assert.Single(bed.Meta.TemplateSends).Target);
    }

    [Fact]
    public async Task ACustomerWhoAlreadyHasAConversationOnThatConnectionGetsTheSendThereWith200()
    {
        var bed = Bed();
        var customer = bed.Customers.Add();
        var existing = bed.OpenConversation(lastInboundHoursAgo: 90, assignedTo: bed.MemberId, customerId: customer.CustomerId);

        var result = await bed.StartHandler().HandleAsync(Command(bed, customer.CustomerId), Ct);

        Assert.False(result.Created);
        Assert.Equal(existing.Id, result.Body.Conversation.Id);
        Assert.Single(bed.Conversations.Rows);
        Assert.Equal("Template", result.Body.Message.Kind);
    }

    [Fact]
    public async Task AnExistingBlockedConversationRefusesTheTemplateWithoutMeta()
    {
        var bed = Bed();
        var customer = bed.Customers.Add();
        bed.OpenConversation(lastInboundHoursAgo: 90, assignedTo: bed.MemberId, customerId: customer.CustomerId, blocked: true);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.StartHandler().HandleAsync(Command(bed, customer.CustomerId), Ct));

        Assert.Equal(MessagingErrorCodes.ContactBlocked, error.Code);
        Assert.Empty(bed.Meta.TemplateSends);
    }

    [Fact]
    public async Task AnExistingConversationOfSomeoneElseKeepsTheAssignmentRule()
    {
        var bed = Bed();
        var customer = bed.Customers.Add();
        bed.OpenConversation(lastInboundHoursAgo: 90, assignedTo: Guid.CreateVersion7(), customerId: customer.CustomerId);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.StartHandler().HandleAsync(Command(bed, customer.CustomerId), Ct));

        Assert.Equal(MessagingErrorCodes.AssignedToOther, error.Code);
        Assert.Empty(bed.Meta.TemplateSends);
    }

    [Fact]
    public async Task AnUnknownCustomerIs404()
    {
        var bed = Bed();

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => bed.StartHandler().HandleAsync(Command(bed, Guid.CreateVersion7()), Ct));

        Assert.Equal("messaging.customer.not_found", error.Code);
        Assert.Empty(bed.Opener.Calls);
    }

    [Fact]
    public async Task AnInactiveCustomerIsAlso404()
    {
        var bed = Bed();
        var customer = bed.Customers.Add(active: false);

        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => bed.StartHandler().HandleAsync(Command(bed, customer.CustomerId), Ct));

        Assert.Equal("messaging.customer.not_found", error.Code);
    }

    [Fact]
    public async Task ACustomerWithoutPhoneNorBsuidIsUnreachable()
    {
        var bed = Bed();
        var customer = bed.Customers.Add(phone: null);

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.StartHandler().HandleAsync(Command(bed, customer.CustomerId), Ct));

        Assert.Equal("messaging.customer.unreachable", error.Code);
        Assert.Empty(bed.Opener.Calls);
    }

    [Fact]
    public async Task AConnectionThatIsNotActiveIs422WithoutOpeningAnything()
    {
        var bed = Bed();
        var customer = bed.Customers.Add();
        bed.Connections.Sender = null;

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.StartHandler().HandleAsync(Command(bed, customer.CustomerId), Ct));

        Assert.Equal(MessagingErrorCodes.ConnectionUnavailable, error.Code);
        Assert.Empty(bed.Opener.Calls);
    }

    [Fact]
    public async Task ATemplateThatIsNotAvailableOpensNoConversation()
    {
        var bed = Bed();
        var customer = bed.Customers.Add();

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.StartHandler().HandleAsync(Command(bed, customer.CustomerId, name: "otra"), Ct));

        Assert.Equal("messaging.template.not_available", error.Code);
        Assert.Empty(bed.Opener.Calls);
        Assert.Empty(bed.Conversations.Rows);
    }

    [Fact]
    public async Task AParameterMismatchOpensNoConversation()
    {
        var bed = Bed();
        var customer = bed.Customers.Add();
        var command = Command(bed, customer.CustomerId) with { Parameters = new Dictionary<string, string?> { ["1"] = "Ana" } };

        var error = await Assert.ThrowsAsync<MessagingDomainException>(() => bed.StartHandler().HandleAsync(command, Ct));

        Assert.Equal("messaging.template.parameters_mismatch", error.Code);
        Assert.Empty(bed.Opener.Calls);
    }

    [Fact]
    public async Task AnotherTenantIs403()
    {
        var bed = Bed();
        var customer = bed.Customers.Add();

        await Assert.ThrowsAsync<RequestForbiddenException>(() => bed.StartHandler(tenantId: Guid.CreateVersion7()).HandleAsync(Command(bed, customer.CustomerId), Ct));

        Assert.Empty(bed.Opener.Calls);
    }

    [Fact]
    public async Task AnInvalidBodyIsAValidationError()
    {
        var bed = Bed();

        await Assert.ThrowsAsync<ValidationException>(() => bed.StartHandler().HandleAsync(Command(bed, customerId: null), Ct));
    }
}
