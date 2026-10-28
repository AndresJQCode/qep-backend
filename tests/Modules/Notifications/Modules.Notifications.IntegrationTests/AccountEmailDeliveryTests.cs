using Modules.Identity.Application;
using Modules.Notifications.Application;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

public sealed class AccountEmailDeliveryTests
{
    [Fact]
    public async Task AVerificationEventSendsOneMailAndRecordsItWithoutTenant()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(cs, channel, runDeliveryWorkers: true);
        var (_, ownerUserId, _) = await RegisterOwnerAsync(factory);

        await InsertOutboxAsync(cs, IdentityIntegrationEvents.EmailVerificationRequested, $$"""{"userId":"{{ownerUserId}}","token":"tok_1"}""");

        var row = await WaitForNotificationAsync(cs, ownerUserId, EmailVerificationEmailTemplate.TemplateRef);
        Assert.Equal("Sent", row!.Status);
        Assert.Null(await TenantIdOfAsync(cs, ownerUserId, EmailVerificationEmailTemplate.TemplateRef));
        Assert.Contains("http://localhost:3002/verify-email?token=tok_1", Assert.Single(channel.Sent).TextBody);
    }

    [Fact]
    public async Task AResetEventSendsTheResetLink()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(cs, channel, runDeliveryWorkers: true);
        var (_, ownerUserId, _) = await RegisterOwnerAsync(factory);

        await InsertOutboxAsync(cs, IdentityIntegrationEvents.PasswordResetRequested, $$"""{"userId":"{{ownerUserId}}","token":"tok_2"}""");

        var row = await WaitForNotificationAsync(cs, ownerUserId, PasswordResetEmailTemplate.TemplateRef);
        Assert.Equal("Sent", row!.Status);
        Assert.Null(await TenantIdOfAsync(cs, ownerUserId, PasswordResetEmailTemplate.TemplateRef));
        var mail = Assert.Single(channel.Sent);
        Assert.Equal("Restablece tu contraseña", mail.Subject);
        Assert.Contains("http://localhost:3002/reset-password?token=tok_2", mail.TextBody);
    }

    [Fact]
    public async Task AnExistingAccountEventSendsLoginAndForgotLinks()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(cs, channel, runDeliveryWorkers: true);
        var (_, ownerUserId, _) = await RegisterOwnerAsync(factory);

        await InsertOutboxAsync(cs, IdentityIntegrationEvents.ExistingAccountNotice, $$"""{"userId":"{{ownerUserId}}"}""");

        var row = await WaitForNotificationAsync(cs, ownerUserId, ExistingAccountEmailTemplate.TemplateRef);
        Assert.Equal("Sent", row!.Status);
        Assert.Null(await TenantIdOfAsync(cs, ownerUserId, ExistingAccountEmailTemplate.TemplateRef));
        var mail = Assert.Single(channel.Sent);
        Assert.Equal("Ya tienes una cuenta", mail.Subject);
        Assert.Contains("http://localhost:3002/login", mail.TextBody);
        Assert.Contains("http://localhost:3002/forgot-password", mail.TextBody);
    }

    [Fact]
    public async Task AVerificationEventWithoutTokenIsFailedWithoutSending()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        var channel = new RecordingEmailChannel();
        using var factory = new NotificationsApiFactory(cs, channel, runDeliveryWorkers: true);
        var (_, ownerUserId, _) = await RegisterOwnerAsync(factory);

        await InsertOutboxAsync(cs, IdentityIntegrationEvents.EmailVerificationRequested, $$"""{"userId":"{{ownerUserId}}"}""");

        var row = await WaitForNotificationAsync(cs, ownerUserId, EmailVerificationEmailTemplate.TemplateRef);
        Assert.Equal("Failed", row!.Status);
        Assert.Equal("email_token_unavailable", row.FailureReason);
        Assert.Empty(channel.Sent);
    }
}
