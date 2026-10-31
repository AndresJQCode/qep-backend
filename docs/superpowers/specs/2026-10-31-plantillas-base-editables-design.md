# Plantillas de cotización y de cobro con texto editable

Estado: diseño en revisión. Backend en `qep-backend`, pantalla en `qep-frontend`.

## Por qué

En `/integrations/whatsapp/{connectionId}/templates`, «Plantilla de cotización» y «Crear plantillas de cobro» mandan a Meta un texto
fijo en el código. El 2026-10-31 Meta rechazó `qep_cotizacion` en producción con `400`, `code 100`, `subcode 2388293`
(«Parameters words ratio exceeds limit»): 4 variables para 8 palabras fijas. El tenant no tenía forma de arreglarlo, y QEP lo mostraba
como tope de plantillas.

**Prerrequisito:** el arreglo de ese mensaje está sin commit en `fix/template-limit-translation`. Se commitea y se mergea a `develop` antes de
empezar, y esta rama se actualiza desde ahí: sin él, un rechazo por densidad seguiría llegando como tope de plantillas.

Lo que quiere el owner: QEP da un texto base y, **antes de enviarlo a aprobación**, el tenant puede cambiar el texto, quitar variables y
agregar las permitidas.

## Metas

- Los dos botones abren un editor con el texto base, no crean la plantilla directo.
- El admin edita el texto y usa solo variables de una lista cerrada, con nombre legible.
- Lo que se aprueba es lo que se envía y lo que la bandeja muestra, plantilla por plantilla.
- Si Meta rechaza, el admin ve la razón y reintenta desde el texto que había escrito.
- Las plantillas ya aprobadas siguen funcionando sin que nadie haga nada.

## Fuera de alcance

- Editar una plantilla ya aprobada. Meta no lo permite; sigue siendo borrarla desde la pantalla y crearla de nuevo.
- Editar encabezado, botones, idioma o categoría, o agregar pie.
- Variables libres. Sin una fuente de datos, el envío no sabría con qué llenarlas.
- La plantilla de Zenvia.

## Qué sería un fracaso

- Una cotización sale con una variable sin llenar o con el texto de otro tenant.
- Una plantilla aprobada antes de este cambio deja de enviarse.
- Un envío falla sin error de compilación porque las variables de la fila no coinciden con lo que manda el backend.

## Variables

**Regla del owner: siempre con nombre, nunca `{{1}}`.** Las plantillas nuevas de cobro pasan a `parameter_format = NAMED`. `POSITIONAL`
solo se lee, para las filas ya existentes.

Cotización (`Quotation`):

| Etiqueta | Clave | Valor al enviar |
| --- | --- | --- |
| Nombre del contacto | `nombre_contacto` | El nombre que se manda hoy como `fullname` |
| Nombre del asesor | `asesor` | Quien hizo clic en enviar (`SentByMemberId`), decisión del owner |
| Nombre de la empresa | `empresa` | Nombre del tenant (`IMessagingTenantNames`) |
| Saludo según la hora | `saludo` | `TimeOfDayGreeting.For` con la hora local del tenant, como en campañas |
| Número de cotización | `numero_cotizacion` | El que se manda hoy como `order_number` |
| Valor total | `valor_total` | El que se manda hoy como `total` |
| Vigencia | `vigencia` | El que se manda hoy como `valid_until`; enviar ya exige la fecha |

Recordatorio de cobro (`BillingReminder`): las cuatro fijas más `servicio` (Servicio), `valor` (Valor) y `vencimiento` (Vencimiento).

Vencido (`BillingOverdue`): las cuatro fijas más `cantidad_cobros` (Cantidad de cobros) y `total_vencido` (Total vencido).

**Regla del owner (2026-10-31): un solo nombre por variable, en español, en todas las pantallas.** Claves en español en `snake_case` sin
tildes, porque Meta solo admite minúsculas, números y guion bajo. La misma clave y la misma etiqueta en el editor, el diseñador de plantillas,
las campañas y las respuestas rápidas. Este trabajo cambia también lo que hoy dice otra cosa: la fuente `customerName` de campañas se muestra
«Nombre del contacto» (no «Nombre del cliente»), la ayuda de `nombre_contacto` en respuestas rápidas dice lo mismo, y los ejemplos del
diseñador (`first_name`, `{{order}}`) pasan a `nombre_contacto` y `numero_cotizacion`.

**Las cuatro primeras son las variables fijas de `develop`** (`feature/variables-fijas`, 2026-10-31): mismos nombres, mismas etiquetas
y mismo ejemplo que `FIXED_VARIABLE_NAMES` del frontend, con doble llave porque Meta lo exige. Esa lista es la autoridad: si crece, esta
también. El frontend reutiliza sus etiquetas, no las copia. El saludo se resuelve en el backend también en cotizaciones, aunque el asesor
esté en un navegador: el envío ocurre allá y `greetingAt` no participa.

`fullname`, `order_number`, `total` y `valid_until` dejan de ofrecerse, pero las plantillas ya aprobadas los llevan y Meta no deja
cambiarlas: el envío los sigue llenando, con el mismo valor de su clave en español. Son el único lugar donde sobreviven.

En cobros, `asesor` es el encargado del perfil de cobro, que ya es `SentByMemberId`: el envío es automático y no hay clic.

`asesor` sale de `FindDisplayNameAsync`, nunca del correo, igual que `{asesor}` en respuestas rápidas.

## Textos base

Cotización:

> ¡Hola, {{nombre_contacto}}! Te compartimos en este mensaje la cotización *{{numero_cotizacion}}*, por un valor total de *{{valor_total}}*.
> Esta cotización estará vigente hasta el *{{vigencia}}*. Si tienes alguna pregunta, responde este mensaje y con gusto te ayudamos.

Cobro: el texto aprobado por el owner el 2026-10-29, con las mismas palabras y claves con nombre en lugar de `{{1}}`.

Encabezado de documento (PDF) y categoría `UTILITY` siguen fijos. **Sin pie:** las plantillas nuevas de cotización ya no llevan «Este
mensaje fue generado automáticamente.» (owner, 2026-10-31). Una fila existente conserva el suyo, porque Meta no deja editarla; la bandeja
lo dibuja si la fila lo tiene.

## Cómo funciona

**Contrato HTTP (BFF).**

- `GET …/template-management/quotation/draft` y `GET …/template-management/billing/draft` devuelven lo que el editor dibuja: texto
  inicial, variables permitidas con clave, etiqueta y ejemplo, y el motivo del rechazo si lo hay. El texto inicial es el de la plantilla
  rechazada si existe; si hay una `External` compatible, su cuerpo; si no, el base.
- `POST …/quotation` recibe `{ body }`. `POST …/billing` recibe `{ reminderBody, overdueBody }`.
- El cuerpo es opcional y sin él se usa el texto base, para poder desplegar backend antes que frontend.

**Validación.** Validador de FluentValidation, con el mapa `errors`: requerido, máximo 1024 caracteres, forma `{{clave}}` y clave dentro de
la lista de ese tipo. Un error marca el campo; en cobros, la clave dice cuál de los dos textos.

**Crear.** Sigue el aprovisionamiento actual (versiones, cuarentena, borrar la rechazada después de crear). Solo cambia de dónde sale el
cuerpo. Los ejemplos que exige Meta salen de la lista de variables, solo de las usadas.

**Cobros, una plantilla a la vez.** Hoy el handler guarda las dos al final en una sola transacción: si Meta acepta el recordatorio y rechaza
el vencido, la aceptada queda en Meta sin fila y cada reintento crea otra versión. Con texto libre ese rechazo será lo común. Cada
plantilla se guarda en su propia transacción, justo después de que Meta la acepte. Ante un rechazo se detiene y responde el `422` de
siempre; el `detail` empieza con el nombre de la plantilla («Recordatorio: …»), que la pantalla ya muestra tal cual. Lo aceptado queda.

**Estados mezclados en cobros.** El editor solo deja escribir en las que no están vivas; una viva se ve de solo lectura y su campo no se
manda. El botón aparece mientras falte alguna de las dos, para que borrar una viva tenga salida. Una rechazada `POSITIONAL` arranca desde el
texto base, porque `{{1}}` no pasa el validador.

**Reintento.** Un rechazo síncrono de Meta no deja fila, así que la ventana sigue abierta con lo escrito. No se guarda en el servidor: si el
admin la cierra, vuelve al texto inicial. Un rechazo posterior, en la revisión de Meta, sí deja fila y el borrador arranca de ahí.

**Enviar.** Cada plantilla ya guarda su texto en `components`. La fila aprobada es la autoridad:

1. Las lecturas `FindApprovedAsync` devuelven también `parameter_format` y `components`.
2. Quien envía arma todos los valores disponibles, por clave.
3. Se mandan solo las variables que la fila usa, con su nombre. Con `POSITIONAL` se conserva el orden de la definición vieja.
4. El texto de la bandeja se dibuja desde el cuerpo guardado de la fila, no desde una constante. Así una plantilla aprobada con el texto
   viejo se sigue mostrando como se envió.

Las reglas de `TemplateParameterRules` se aplican solo a los valores que la fila usa.

**Adopción.** Hoy `Matches` compara contra el texto fijo, y con texto libre eso deja de tener sentido. Una `External` con el nombre base
se adopta si tiene encabezado de documento, sin botones, sus variables están en la lista o son claves viejas (`fullname`, `order_number`, `total`, `valid_until`) y su cuerpo es igual al enviado, tenga pie o no. Si es
compatible, el borrador ya trae ese cuerpo, así que el camino normal la adopta. Si el admin lo cambió, responde `quotation_conflict`, como hoy.
Cobros no adopta, como hoy.

**Pantalla.** Una ventana propia con el mensaje, botones para insertar variables y vista previa de lo que verá el cliente. Cobros muestra
los dos textos en la misma ventana y los manda juntos. Reutiliza `template-body-editor.tsx`, sin el campo de variable libre.

**Aviso de densidad.** `lintTemplateBody` ya tiene `too_many_variables` (mínimo `2x + 1` palabras), pero cuenta signos sueltos como palabras
y por eso deja pasar el texto que Meta rechazó. Cuenta solo lo que tenga letras o números. Es un aviso, no bloquea: el umbral de Meta no
está publicado.

## Decisiones del diseño, no del owner

- La pantalla es la ventana propia (boceto A). El owner no eligió entre A y B.
- Redacción del texto base de cotización.
- Sin nombre visible del asesor, `asesor` usa el nombre de la empresa: Meta rechaza un valor vacío y un envío no debería caer por eso.
- `saludo` entra a las dos listas, por ser variable fija, aunque el owner no lo nombró para estas plantillas. No va en el texto base.
- Lista de variables de cobro y sus nombres, derivados de los ejemplos actuales.
- En cobros, guardar lo que Meta aceptó y parar en el primer rechazo, en vez de deshacer lo aceptado.
- Un texto rechazado al crear no se guarda en el servidor; cerrar la ventana lo pierde.

## Riesgos

- **Meta puede reclasificar un texto editado como `MARKETING`.** No se manda `allow_category_change` (D-T10). Comprueba qué hace el espejo
  con esa categoría y que el envío siga funcionando.
- **Las cuatro variables de cotización ya no van siempre.** Un cambio en el envío se prueba con `QuotationTemplateOutboundTests` y con una
  fila con solo parte de ellas.

## Queda para quien construye

- Forma exacta de los DTO y dónde vive la lista de variables, siempre en Messaging y con el motivo escrito en el DTO.
- Cómo pasar los valores de cobro por clave entre `ServiceContractsMetaWhatsAppSender` y `BillingTemplateOutbound`.
- Un commit por repo y orden de despliegue: backend, luego frontend.
- Actualizar README y la entrada de `CLAUDE.md` que describa el texto como fijo.
