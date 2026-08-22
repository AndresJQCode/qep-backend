using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Remapea las referencias de rol de las membresías vivas al nuevo catálogo
    /// (`admin` / `advisor` / `billing`). No hay cambio de esquema: `roles` sigue siendo
    /// `text[]`; lo que cambia son los valores, que dejaron de existir en el catálogo cuando
    /// `tenancy.owner` y `tenancy.member` se reemplazaron. Una membresía que quede apuntando a
    /// una referencia desconocida resuelve a cero permisos —`RoleCatalog.PermissionsFor`
    /// devuelve una colección vacía— así que sin esta migración el efecto es que todos los
    /// miembros existentes pierden el acceso en silencio, sin ningún 500 que lo delate.
    ///
    /// Es un rename de referencia, no un cambio de autoridad: nadie gana ni pierde permisos.
    /// Por eso se hace en SQL y no por el agregado — no corresponde emitir
    /// `MembershipRolesChangedDomainEvent` ni asentar auditoría por un cambio que no altera
    /// lo que cada persona puede hacer.
    ///
    /// `billing` no aparece acá: es un rol nuevo, sin nadie asignado todavía.
    /// </summary>
    public partial class RenameSystemRolesToAdminAdvisorBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE tenancy.memberships
                SET roles = array_replace(
                        array_replace(roles, 'tenancy.owner', 'admin'),
                        'tenancy.member', 'advisor')
                WHERE roles && ARRAY['tenancy.owner', 'tenancy.member'];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Sólo revierte las dos referencias que Up renombró. Una membresía con `billing`
            // queda con esa referencia intacta y sin equivalente en el catálogo viejo: revertir
            // el código sin revertir esas asignaciones deja a esa persona sin permisos. Es
            // deliberado — inventarle un rol viejo sería devolverle autoridad que nunca tuvo.
            migrationBuilder.Sql(
                """
                UPDATE tenancy.memberships
                SET roles = array_replace(
                        array_replace(roles, 'admin', 'tenancy.owner'),
                        'advisor', 'tenancy.member')
                WHERE roles && ARRAY['admin', 'advisor'];
                """);
        }
    }
}
