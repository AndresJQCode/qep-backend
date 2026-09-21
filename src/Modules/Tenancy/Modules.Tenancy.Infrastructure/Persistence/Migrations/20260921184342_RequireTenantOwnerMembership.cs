using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// <c>owner_membership_id</c> pasa a ser obligatorio. Desde acá no hay tenant sin autoridad, y
    /// la guarda del agregado —no se puede suspender, quitar ni degradar al owner— vale siempre.
    ///
    /// El scaffolding proponía rellenar los nulos con <c>Guid.Empty</c>. Eso es peor que el
    /// problema que viene a cerrar: dejaría tenants apuntando a una membresía que no existe, y la
    /// guarda protegiendo a nadie mientras aparenta estar puesta.
    /// </summary>
    public partial class RequireTenantOwnerMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // El único tenant que quedaba sin owner era el `qcode-demo` que sembraba
            // TenancyDatabaseInitializer en Development: se creaba sin ninguna membresía, así que
            // los permisos no resolvían y cualquier request contra él daba 403. Se borra sólo esa
            // forma exacta —sin owner Y sin ninguna membresía—, que es un tenant al que nadie
            // pudo haber entrado nunca.
            migrationBuilder.Sql(
                """
                DELETE FROM tenancy.tenants AS t
                WHERE t.owner_membership_id IS NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM tenancy.memberships AS m
                      WHERE m.tenant_id = t.id);
                """);

            // Sin default a propósito. Si acá queda algún tenant en NULL es uno que sí tiene
            // membresías, y elegirle owner no es decisión de una migración: el ALTER falla, se ve
            // en el arranque, y alguien decide a quién nombrar.
            migrationBuilder.Sql(
                """
                ALTER TABLE tenancy.tenants
                ALTER COLUMN owner_membership_id SET NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Los tenants borrados no vuelven: eran filas inservibles y recrearlas sería inventar
            // datos. Volver a admitir NULL es todo lo que esta vuelta atrás puede prometer.
            migrationBuilder.Sql(
                """
                ALTER TABLE tenancy.tenants
                ALTER COLUMN owner_membership_id DROP NOT NULL;
                """);
        }
    }
}
