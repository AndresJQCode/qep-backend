namespace BuildingBlocks.Infrastructure;

/// <summary>
/// Candado de Postgres (advisory, de transacción) que serializa el borrado de un cliente contra quien le escribe una
/// referencia (cotización, pedido, préstamo, contrato de servicio). Quien escribe la referencia lo toma
/// <see cref="Shared"/>; quien borra el cliente, <see cref="Exclusive"/>.
///
/// Devuelve <see cref="FormattableString"/> (BCL) y no depende de EF: cada módulo lo ejecuta con su propio contexto
/// (<c>ExecuteSql</c> / <c>SqlQuery</c>), que lo convierte en parámetros.
///
/// Orden obligatorio: primero el candado y después <see cref="Exists"/> (P1). <c>Exists</c> lee
/// <c>customers.customers</c> por nombre, y el escritor lo corre **dentro** de su transacción, ya con el candado
/// compartido tomado: si el borrado ganó la carrera, el escritor espera al candado, y al despertar ve la fila ya
/// confirmada como borrada. Leer antes del candado vería un cliente que está por desaparecer.
///
/// Esa garantía vale sólo en READ COMMITTED (el nivel por defecto): en REPEATABLE READ o SERIALIZABLE la foto de la
/// transacción se toma en la primera sentencia, el SELECT del candado y antes de esperar, así que <see cref="Exists"/> seguiría
/// viendo la fila borrada. Por eso el escritor corre <see cref="Shared"/> y <see cref="Exists"/> en una transacción READ
/// COMMITTED, y <see cref="Exists"/> va como sentencia aparte, después del candado, nunca combinada con él.
/// </summary>
public static class CustomerReferenceLock
{
    public static string KeyFor(Guid tenantId, Guid customerId) =>
        $"customers.customer-reference:{tenantId:N}:{customerId:N}";

    public static FormattableString Exclusive(Guid tenantId, Guid customerId)
    {
        var key = KeyFor(tenantId, customerId);
        return $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))";
    }

    public static FormattableString Shared(Guid tenantId, Guid customerId)
    {
        var key = KeyFor(tenantId, customerId);
        return $"SELECT pg_advisory_xact_lock_shared(hashtextextended({key}, 0))";
    }

    // Para SqlQuery<bool>: la columna se llama "Value".
    public static FormattableString Exists(Guid tenantId, Guid customerId) =>
        $"SELECT EXISTS (SELECT 1 FROM customers.customers WHERE tenant_id = {tenantId} AND id = {customerId}) AS \"Value\"";
}
