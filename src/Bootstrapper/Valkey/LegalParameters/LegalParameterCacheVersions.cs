using Modules.Hr.Application;

namespace Bootstrapper.Valkey.LegalParameters;

/// <summary>Sin caché que invalidar (pruebas, o hasta que el área se registre): subir la versión no hace nada.</summary>
internal sealed class NullLegalParameterCacheVersion : ILegalParameterCacheVersion
{
    public Task BumpAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
