using Microsoft.EntityFrameworkCore;
using Modules.Authorization.Domain;

namespace Modules.Authorization.Infrastructure.Persistence;

public sealed class AuthorizationDbContext(DbContextOptions<AuthorizationDbContext> options)
    : DbContext(options)
{
    public DbSet<Role> Roles => Set<Role>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var role = modelBuilder.Entity<Role>();
        role.ToTable("roles", "authorization");
        role.HasKey(value => value.Id);
        role.Property(value => value.Id)
            .HasColumnName("id")
            .HasConversion(id => id.Value, value => new RoleId(value))
            .ValueGeneratedNever();
        role.Property(value => value.TenantId).HasColumnName("tenant_id");
        role.Property(value => value.Key)
            .HasColumnName("key")
            .HasMaxLength(Role.KeyMaxLength);
        role.Property(value => value.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(Role.DisplayNameMaxLength);
        role.Property(value => value.Description)
            .HasColumnName("description")
            .HasMaxLength(Role.DescriptionMaxLength);
        role.Property(value => value.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        role.Property(value => value.CreatedAt).HasColumnName("created_at");
        role.Property(value => value.UpdatedAt).HasColumnName("updated_at");

        // Unico por (tenant, clave). La clave es lo que viaja en la membresia, asi que dos
        // filas con la misma en un tenant dejan a `PermissionsFor` eligiendo una en silencio
        // — el mismo fallo que `Role.Create` evita contra los roles de sistema, pero entre
        // custom, sin indice, no lo evita nadie. El nombre importa: cuando haya que traducir
        // el 23505 se discrimina por indice, no por SqlState. Es la leccion de SDD-CT-06.
        role.HasIndex(value => new { value.TenantId, value.Key })
            .IsUnique()
            .HasDatabaseName("IX_roles_tenant_key");

        // Los permisos son una coleccion de valor del agregado: no tienen identidad ni ciclo
        // de vida propio, y solo se leen a traves del rol. Por eso van como coleccion
        // primitiva y no como entidad hija — inventarle una entidad a `List<string>` seria
        // modelar la tabla en vez del dominio.
        role.PrimitiveCollection<List<string>>("_permissions")
            .HasColumnName("permissions");

        // `Permissions` es la vista de solo lectura de ese campo. Sin ignorarla, EF intenta
        // mapear las dos y falla al arrancar.
        role.Ignore(value => value.Permissions);
    }
}
