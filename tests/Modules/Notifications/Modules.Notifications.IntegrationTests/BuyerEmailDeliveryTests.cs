using System.Text.Json;
using Modules.Buyers.Application;
using Modules.Notifications.Application;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

public sealed class BuyerEmailDeliveryTests
{
    private const string Host = "acme.lulocrm.com";

    [Fact]
    public async Task AVerificationEventSendsOneMailToThePayloadEmail()
    {
        var mail = await DeliverAsync(
            BuyerIntegrationEvents.EmailVerificationRequested, BuyerVerificationEmailTemplate.TemplateRef, "tok_1");

        Assert.Equal("Confirma tu cuenta para comprar en Acme", mail.Subject);
        Assert.Contains($"https://{Host}/cuenta/verificar?token=tok_1", mail.TextBody);
    }

    [Fact]
    public async Task AnExistingAccountEventSendsLoginAndForgotLinks()
    {
        var mail = await DeliverAsync(
            BuyerIntegrationEvents.ExistingAccountNotice, BuyerExistingAccountEmailTemplate.TemplateRef, null);

        Assert.Equal("Ya tienes una cuenta para comprar en Acme", mail.Subject);
        Assert.Contains($"https://{Host}/cuenta/ingresar", mail.TextBody);
        Assert.Contains($"https://{Host}/cuenta/olvide", mail.TextBody);
    }

    [Fact]
    public async Task AResetEventSendsTheResetLink()
    {
        var mail = await DeliverAsync(
            BuyerIntegrationEvents.PasswordResetRequested, BuyerPasswordResetEmailTemplate.TemplateRef, "tok_2");

        Assert.Equal("Restablece tu contraseña", mail.Subject);
        Assert.Contains($"https://{Host}/cuenta/restablecer?token=tok_2", mail.TextBody);
    }

    [Fact]
    public async Task AnAccountDeletedEventSendsTheConfirmation()
    {
        var mail = await DeliverAsync(
            BuyerIntegrationEvents.AccountDeleted, BuyerAccountDeletedEmailTemplate.TemplateRef, null);

        Assert.Equal("Eliminamos tu cuenta", mail.Subject);
    }

    [Fact]
    public async Task AVerificationWithoutTokenIsRecordedAsFailed()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(cs, channel, runDeliveryWorkers: true);
        _ = factory.Services;
        var accountId = Guid.CreateVersion7();

        await InsertOutboxAsync(
            cs, BuyerIntegrationEvents.EmailVerificationRequested, Payload(accountId, "ana@example.com", null));

        var row = await WaitForNotificationAsync(cs, accountId, BuyerVerificationEmailTemplate.TemplateRef);
        Assert.Equal("Failed", row!.Status);
        Assert.Equal("email_token_unavailable", row.FailureReason);
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task APayloadWithoutEmailIsRecordedAsFailed()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(cs, channel, runDeliveryWorkers: true);
        _ = factory.Services;
        var accountId = Guid.CreateVersion7();

        await InsertOutboxAsync(cs, BuyerIntegrationEvents.AccountDeleted, Payload(accountId, string.Empty, null));

        var row = await WaitForNotificationAsync(cs, accountId, BuyerAccountDeletedEmailTemplate.TemplateRef);
        Assert.Equal("Failed", row!.Status);
        Assert.Equal("recipient_email_unavailable", row.FailureReason);
        Assert.Empty(channel.Sent);
    }

    [Theory]
    [InlineData("acme\".lulocrm.com")]
    [InlineData("acme lulocrm.com")]
    [InlineData("")]
    public async Task AnInvalidStoreHostIsRecordedAsFailedWithoutSending(string host)
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(cs, channel, runDeliveryWorkers: true);
        _ = factory.Services;
        var accountId = Guid.CreateVersion7();

        await InsertOutboxAsync(
            cs,
            BuyerIntegrationEvents.EmailVerificationRequested,
            Payload(accountId, "ana@example.com", "tok_1", host));

        var row = await WaitForNotificationAsync(cs, accountId, BuyerVerificationEmailTemplate.TemplateRef);
        Assert.Equal("Failed", row!.Status);
        Assert.Equal("store_host_invalid", row.FailureReason);
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task AResetWithoutTokenIsRecordedAsFailed()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(cs, channel, runDeliveryWorkers: true);
        _ = factory.Services;
        var accountId = Guid.CreateVersion7();

        await InsertOutboxAsync(
            cs, BuyerIntegrationEvents.PasswordResetRequested, Payload(accountId, "ana@example.com", null));

        var row = await WaitForNotificationAsync(cs, accountId, BuyerPasswordResetEmailTemplate.TemplateRef);
        Assert.Equal("Failed", row!.Status);
        Assert.Equal("email_token_unavailable", row.FailureReason);
        Assert.Empty(channel.Sent);
    }

    private static async Task<EmailMessage> DeliverAsync(string eventName, string templateRef, string? token)
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(cs, channel, runDeliveryWorkers: true);
        _ = factory.Services;
        var accountId = Guid.CreateVersion7();

        await InsertOutboxAsync(cs, eventName, Payload(accountId, "ana@example.com", token));

        var row = await WaitForNotificationAsync(cs, accountId, templateRef);
        Assert.Equal("Sent", row!.Status);
        Assert.Equal("ana@example.com", row.RecipientAddress);
        Assert.Null(await TenantIdOfAsync(cs, accountId, templateRef));
        var mail = Assert.Single(channel.Sent);
        Assert.Equal("ana@example.com", mail.ToAddress);
        return mail;
    }

    private static string Payload(Guid accountId, string email, string? token, string host = Host) =>
        JsonSerializer.Serialize(
            new BuyerEmailPayload(accountId, email, host, "Acme", token), JsonSerializerOptions.Web);
}
