namespace Modules.Hr.Application;

/// <summary>
/// El contador de versión de la caché de valores legales (spec 2026-11-22, «Caché»). Quien escribe lo sube <b>después</b> del commit
/// para que los lectores recarguen. <b>Nunca lanza</b>: un fallo de Valkey no puede convertir en error una escritura que ya se
/// confirmó; el adaptador lo cuenta y lo registra, y el TTL de la caché cierra el hueco.
/// </summary>
public interface ILegalParameterCacheVersion
{
    Task BumpAsync(CancellationToken cancellationToken);
}
