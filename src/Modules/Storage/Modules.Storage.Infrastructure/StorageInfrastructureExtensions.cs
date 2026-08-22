using Amazon.Runtime;
using Amazon.S3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Storage.Application;
using Modules.Storage.Infrastructure.Imaging;
using Modules.Storage.Infrastructure.ObjectStorage;
using Modules.Storage.Infrastructure.Persistence;
using Modules.Storage.Infrastructure.Scanning;

namespace Modules.Storage.Infrastructure;

public static class StorageInfrastructureExtensions
{
    public static IServiceCollection AddStorageInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("QepDatabase")
            ?? throw new InvalidOperationException(
                "Connection string 'QepDatabase' is required.");

        services.AddDbContext<StorageDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    "storage")));

        var section = configuration.GetSection(StorageOptions.SectionName);
        services.AddOptions<StorageOptions>().Bind(section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();

        services.AddScoped<IFileResourceRepository, FileResourceRepository>();
        services.AddScoped<IStorageUnitOfWork, StorageUnitOfWork>();
        services.AddScoped<IStorageAuditPublisher, StorageAuditPublisher>();
        services.AddSingleton<IFileContentInspector, FileContentInspector>();
        services.AddSingleton<IImageVariantGenerator, ImageSharpVariantGenerator>();
        services.AddSingleton<IFileScanner>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<StorageOptions>>();
            return options.Value.ClamAv.Enabled
                ? new ClamAvFileScanner(options)
                : new NoopFileScanner();
        });
        services.AddSingleton<IAmazonS3>(CreateR2Client);
        services.AddSingleton<IObjectStorage, R2ObjectStorage>();
        services.AddSingleton<IPublicObjectStorage, R2PublicObjectStorage>();
        services.AddHostedService<StagingCleanupWorker>();

        return services;
    }

    private static IAmazonS3 CreateR2Client(IServiceProvider serviceProvider)
    {
        var r2 = serviceProvider.GetRequiredService<IOptions<StorageOptions>>().Value.R2;
        var endpoint = string.IsNullOrWhiteSpace(r2.Endpoint)
            ? $"https://{r2.AccountId}.r2.cloudflarestorage.com"
            : r2.Endpoint;
        var config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "auto",
            // AWSSDK.S3 v4 calcula un checksum CRC32 y lo manda en un trailer HTTP salvo que se
            // le pida lo contrario. R2 no implementa trailers y responde 500
            // "STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER not implemented". Va acá y no en una
            // variable de entorno porque el destino es R2 siempre (ADR 0020): que la aplicación
            // funcione contra su único almacenamiento no puede depender de que alguien recuerde
            // exportar algo antes de arrancar.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        var credentials = new BasicAWSCredentials(r2.AccessKeyId, r2.SecretAccessKey);
        return new AmazonS3Client(credentials, config);
    }
}
