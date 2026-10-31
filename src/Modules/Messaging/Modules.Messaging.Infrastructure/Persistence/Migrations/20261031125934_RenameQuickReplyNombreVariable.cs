using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Messaging.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Sólo datos: <c>{nombre}</c> pasa a <c>{nombre_contacto}</c> en los cuerpos de las respuestas rápidas. Se despliega después del frontend
    /// que resuelve el nombre nuevo: antes de eso, un SPA viejo insertaría <c>{nombre_contacto}</c> literal en el chat.
    /// </summary>
    /// <remarks>
    /// <c>body</c> es <c>varchar(4096)</c> y el reemplazo suma 9 caracteres por aparición. Una fila que no quepa conserva <c>{nombre}</c>,
    /// que el agregado sigue aceptando: un <c>UPDATE</c> sin ese filtro fallaría entero con 22001 y frenaría el despliegue por un texto. La
    /// versión sube donde el cuerpo cambió, así un formulario abierto con el texto viejo recibe 412 en vez de pisarlo; <c>updated_at</c> no
    /// se toca porque nadie editó nada. <c>strpos</c> y no <c>LIKE</c>: el <c>_</c> de <c>{nombre_contacto}</c> es un comodín.
    /// </remarks>
    public partial class RenameQuickReplyNombreVariable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE messaging.quick_replies
                   SET body = replace(body, '{nombre}', '{nombre_contacto}'),
                       version = version + 1
                 WHERE strpos(body, '{nombre}') > 0
                   AND char_length(replace(body, '{nombre}', '{nombre_contacto}')) <= 4096;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // También vuelve lo que nació con {nombre_contacto} después del Up: es lo que el SPA anterior sabe resolver. {saludo} se queda: no
            // tiene equivalente al que volver, y este reemplazo no puede inventarlo.
            migrationBuilder.Sql(
                """
                UPDATE messaging.quick_replies
                   SET body = replace(body, '{nombre_contacto}', '{nombre}'),
                       version = version + 1
                 WHERE strpos(body, '{nombre_contacto}') > 0;
                """);
        }
    }
}
