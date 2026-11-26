using BuildingBlocks.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure;
using Modules.Notifications.Infrastructure.InApp;
using Modules.Notifications.Infrastructure.Messaging;
using Modules.Notifications.Infrastructure.Persistence;
using Modules.Notifications.Infrastructure.TaskNotices;

namespace Modules.Notifications.UnitTests;

/// <summary>
/// Spec 2026-11-13 (Valkey), D-7: los trece consumidores de correo dejan de ser trece hosted services que sondean cada uno por su
/// cuenta y pasan a un solo bucle que drena cada definición en secuencia. Una definición que falla no frena a las demás.
/// </summary>
public sealed class NotificationsDeliveryWorkerTests
{
    [Fact]
    public async Task NotificationsDeliveryWorkerDrainsEveryDefinitionInOneTick()
    {
        var definitions = Enumerable.Range(0, 13).Select(index => new FakeDefinition($"test.consumer-{index}")).ToList();
        var worker = new NotificationsDeliveryWorker(new PollingDoorbell(), definitions, [], NullLogger<NotificationsDeliveryWorker>.Instance);

        await worker.TickAsync(TestContext.Current.CancellationToken);

        Assert.All(definitions, definition => Assert.Equal(1, definition.Drains));
    }

    // Spec 2026-11-22 §12: the bell's consumers share the loop and the doorbell of the emails; they are not hosted services of their own.
    [Fact]
    public async Task TheLoopDrainsTheInAppConsumersToo()
    {
        var email = new FakeDefinition("test.email");
        var inApp = new FakeInApp("test.in-app");
        var failing = new FakeInApp("test.in-app-failing", new InvalidOperationException("boom"));
        var worker = new NotificationsDeliveryWorker(
            new PollingDoorbell(), [email], [failing, inApp], NullLogger<NotificationsDeliveryWorker>.Instance);

        await worker.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, email.Drains);
        Assert.Equal(1, failing.Drains);
        Assert.Equal(1, inApp.Drains);
    }

    [Fact]
    public async Task AFailingDefinitionDoesNotStopTheOthers()
    {
        var first = new FakeDefinition("test.first");
        var failing = new FakeDefinition("test.failing", new InvalidOperationException("boom"));
        var last = new FakeDefinition("test.last");
        var worker = new NotificationsDeliveryWorker(new PollingDoorbell(), [first, failing, last], [], NullLogger<NotificationsDeliveryWorker>.Instance);

        await worker.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, first.Drains);
        Assert.Equal(1, failing.Drains);
        Assert.Equal(1, last.Drains);
    }

    // Review de bloque B: un consumidor que trajo un lote lleno puede tener más; el bucle repite sin esperar la red de seguridad.
    [Fact]
    public async Task ATickReportsMoreWhenAnyDefinitionFilledItsBatch()
    {
        var worker = new NotificationsDeliveryWorker(
            new PollingDoorbell(),
            [new FakeDefinition("test.empty"), new FakeDefinition("test.full", full: true)],
            [],
            NullLogger<NotificationsDeliveryWorker>.Instance);

        Assert.True(await worker.TickAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATickWithoutAFullBatchReportsNoMore()
    {
        var worker = new NotificationsDeliveryWorker(
            new PollingDoorbell(),
            [new FakeDefinition("test.empty"), new FakeDefinition("test.failing", new InvalidOperationException("boom"))],
            [],
            NullLogger<NotificationsDeliveryWorker>.Instance);

        Assert.False(await worker.TickAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OnlyOneNotificationsHostedServiceIsRegistered()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:QepDatabase"] = "Host=localhost;Database=unused",
            })
            .Build();
        var services = new ServiceCollection().AddNotificationsInfrastructure(configuration);

        // One loop for every consumer (emails and the bell's); the only other hosted services are the bell's hourly retention and the task notice escalation (every minute).
        var hosted = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToList();
        Assert.Single(hosted, descriptor => descriptor.ImplementationType == typeof(NotificationsDeliveryWorker));
        Assert.Single(hosted, descriptor => descriptor.ImplementationType == typeof(InAppNotificationRetentionWorker));
        Assert.Single(hosted, descriptor => descriptor.ImplementationType == typeof(TaskNoticeEscalationWorker));
        Assert.Equal(3, hosted.Count);
        // 14 con la invitación de mayorista (spec 2026-11-17); 15 con el código de correo y 17 con los dos avisos de correo
        // cambiado del distribuidor (spec 2026-11-19); 18 con el export de saldos de WMS.
        Assert.Equal(18,services.Count(descriptor => descriptor.ServiceType == typeof(OutboxDeliveryWorker)
            && descriptor.Lifetime == ServiceLifetime.Singleton));
    }

    private sealed class PollingDoorbell : IQueueDoorbell, IDoorbellListener
    {
        public void Ring(string queue)
        {
        }

        public IDoorbellListener Listen(string queue) => this;

        public bool IsLive => false;

        public async Task<bool> WaitAsync(TimeSpan poll, TimeSpan safetyNet, CancellationToken cancellationToken)
        {
            await Task.Delay(poll, cancellationToken);
            return false;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeInApp(string consumer, Exception? failure = null) : InAppOutboxConsumer(null!, NullLogger.Instance)
    {
        public int Drains { get; private set; }

        protected override string Consumer => consumer;

        protected override IReadOnlyCollection<string> EventNames => ["test.event.v1"];

        internal override Task<bool> DrainAsync(CancellationToken cancellationToken)
        {
            Drains++;
            return failure is null ? Task.FromResult(false) : Task.FromException<bool>(failure);
        }

        protected override Task<IReadOnlyList<InAppNotification>> BuildAsync(
            OutboxRecord record, InAppContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDefinition(string consumer, Exception? failure = null, bool full = false)
        : OutboxDeliveryWorker(null!, NullLogger.Instance)
    {
        public int Drains { get; private set; }

        protected override string Consumer => consumer;

        protected override string EventName => "test.event.v1";

        protected override string TemplateRef => "test-template";

        internal override Task<bool> DrainAsync(CancellationToken stoppingToken)
        {
            Drains++;
            return failure is null ? Task.FromResult(full) : Task.FromException<bool>(failure);
        }

        protected override Task<Notification> DeliverAsync(
            OutboxRecord record, DeliveryContext context, CancellationToken stoppingToken) =>
            throw new NotSupportedException();
    }
}
