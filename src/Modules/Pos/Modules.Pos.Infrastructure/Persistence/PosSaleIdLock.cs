using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Modules.Pos.Application;
using Modules.Pos.Domain;

namespace Modules.Pos.Infrastructure.Persistence;

/// <summary>
/// Candado consultivo de transacción de Postgres sobre (tenantId, saleId). La clave es un hash
/// estable de 64 bits —SHA-256 truncado—, no GetHashCode (cambia entre procesos) ni hashtext (función
/// interna de Postgres).
/// </summary>
internal sealed class PosSaleIdLock(PosDbContext dbContext) : IPosSaleIdLock
{
    public async Task AcquireAsync(Guid tenantId, PosSaleId saleId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
        {
            // Un candado de transacción fuera de una transacción se soltaría en el acto.
            throw new InvalidOperationException("The sale id lock needs an open transaction.");
        }

        var key = KeyFor(tenantId, saleId.Value);
        await dbContext.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
    }

    internal static long KeyFor(Guid tenantId, Guid saleId)
    {
        Span<byte> input = stackalloc byte[32];
        tenantId.TryWriteBytes(input[..16], bigEndian: true, out _);
        saleId.TryWriteBytes(input[16..], bigEndian: true, out _);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return BinaryPrimitives.ReadInt64BigEndian(hash);
    }
}
