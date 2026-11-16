using BuildingBlocks.Application.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Tenancy.Application;
using Testcontainers.PostgreSql;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Un host propio por clase con el reloj del tenant movible: las pruebas de plazos fijan «hoy» y lo adelantan. No es el
/// <see cref="SharedSupportHost"/>: cambia <see cref="ITenantClock"/>, que es global al host. Lo comparten sólo las pruebas de una
/// misma clase, que xUnit corre una tras otra; cada una siembra su tenant y fija el reloj al empezar con <see cref="StartTestAsync"/>,
/// así ninguna depende de dónde lo dejó la anterior.
/// </summary>
public sealed class ClockedSupportHost : IAsyncLifetime
{
    /// <summary>La radicación de las pruebas de plazos: viernes 13 de noviembre de 2026, 10:00 en Bogotá.</summary>
    public static readonly DateTimeOffset Reception = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private PostgreSqlContainer? _database;
    private SupportApiHarness.QepApiFactory? _factory;

    internal MutableTenantClock Clock { get; } = new(Reception);

    /// <summary>Los avisos de tiempo real del host: las acciones del ticket prueban que anuncian sólo cuando cambian algo.</summary>
    internal RecordingRealtimePublisher Publisher { get; } = new();

    /// <summary>El host de la clase, con el reloj de vuelta en <see cref="Reception"/>.</summary>
    internal Task<SupportApiHarness.QepApiFactory> StartTestAsync()
    {
        Clock.UtcNow = Reception;
        return Task.FromResult(_factory ?? throw new InvalidOperationException("The clocked host has not been initialized."));
    }

    public async ValueTask InitializeAsync()
    {
        _database = await SupportApiHarness.StartDatabaseAsync();
        _factory = new SupportApiHarness.QepApiFactory(
            _database.GetConnectionString(),
            services =>
            {
                services.RemoveAll<ITenantClock>();
                services.AddSingleton<ITenantClock>(Clock);
                services.RemoveAll<IRealtimePublisher>();
                services.AddSingleton<IRealtimePublisher>(Publisher);
            });
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
}
