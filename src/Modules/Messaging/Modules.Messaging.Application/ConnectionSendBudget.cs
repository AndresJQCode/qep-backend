namespace Modules.Messaging.Application;

/// <summary>
/// Cuántos envíos concedió el presupuesto de una conexión. <see cref="RetryAfter"/> es cuánto falta para el próximo token (o para que termine
/// la pausa tras un 130429) cuando se concedió menos de lo pedido; <see cref="Rate"/> es el ritmo vigente en mensajes por segundo, ya con la
/// subida o la bajada adaptativa aplicadas.
/// </summary>
public sealed record SendGrant(int Granted, TimeSpan RetryAfter, double Rate);

/// <summary>
/// Decisión del owner 2026-10-12 (P3 y P5): el límite de envío de las campañas es de la <b>conexión</b> (el número de Meta), no del proceso.
/// Es un token bucket en Postgres —no hay Redis en el stack— que comparten todas las réplicas, y se adapta a lo que Meta contesta: un
/// 130429 baja el ritmo a la mitad y pausa la conexión 30 s; sin nuevos 130429 el ritmo vuelve a subir de a 1 mps cada 30 s hasta el objetivo
/// (<c>Messaging:Campaigns:MessagesPerSecond</c>). El 131056 no pasa por acá: es un freno del par, no de la conexión.
/// El <c>now</c> entra por parámetro para que las pruebas lo controlen.
/// </summary>
public interface IConnectionSendBudget
{
    /// <summary>Pide hasta <paramref name="requested"/> tokens de golpe y devuelve cuántos se concedieron (0 si la conexión está en pausa o
    /// el cubo está vacío). Atómico entre réplicas: una sola sentencia que rellena, concede y descuenta.</summary>
    Task<SendGrant> AcquireAsync(Guid tenantId, Guid connectionId, int requested, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Meta respondió 130429 para esta conexión. Baja el ritmo a la mitad (mínimo 1) y pausa las concesiones 30 s. Varias respuestas
    /// durante la misma pausa cuentan una sola vez.</summary>
    Task ThrottleAsync(Guid tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken);
}
