# Idioma del producto: español de España y de Latinoamérica (i18n) — diseño

**Fecha:** 2026-11-22 · **Estado:** decisiones del owner tomadas en conversación el 2026-11-22; backend construido en
`claude/festive-noether-ablt1w` · **Repo:** `qep-backend`. El diccionario de la interfaz vive en `qep-frontend`; su spec es
`qep-frontend/docs/superpowers/specs/2026-11-22-i18n-espanol-espana-y-latinoamerica-frontend-design.md`.

## Intención

Que el producto le hable a cada persona en **español de España (`es-ES`)** o **español de Latinoamérica (`es-419`)**. El
frontend traduce la interfaz con su diccionario. El backend guarda el idioma, le dice a la SPA cuál cargar y escribe en ese idioma
los correos que manda.

## Decisiones (owner, 2026-11-22)

| #    | Decisión |
| ---- | -------- |
| D-I1 | **Alcance:** guardar y publicar el idioma, y localizar los correos. Ampliado el mismo día: idioma al registrar, PDF de cotización y pedido, y Excel de cotizaciones. Plantillas de WhatsApp, nombres de roles y permisos, historial y `detail` de los errores quedan fuera (ver «Pendiente»). |
| D-I2 | **Origen:** cada persona elige su idioma **por tenant**, igual que el tema (`UserPreference`, SDD-OD-17). Si no eligió, se usa el idioma del tenant. |
| D-I3 | **Códigos:** `es-419` y `es-ES` (BCP 47). No es `es-CO`: el idioma no es el formato regional, y `DefaultCulture`, `NumberFormat` y `DateFormat` siguen como estaban. |
| D-I4 | **Por defecto:** `es-419`. Los tenants actuales son colombianos y los textos ya están escritos así, así que nadie ve un cambio. |
| D-I5 | **Glosario de España:** «cotización» → «presupuesto»; «radicado» → «número de caso»; y para la interfaz también «celular» → «móvil», «bodega» → «almacén», «abono» → «pago parcial» y «asesor» → «comercial». Tiene que coincidir con el diccionario `es-ES` del frontend. |

## Diseño

- **Catálogo único:** `BuildingBlocks.Domain.Localization.SupportedLanguages` (`All`, `Default`, `TryNormalize`, `OrDefault`,
  `Effective`). Vive en BuildingBlocks porque Tenancy, Identity, Notifications y Support lo usan y no se ven entre sí.
  `Effective(elegido, delTenant)` es **la única regla de resolución**: el elegido si es válido, si no el del tenant, si no
  `es-419`.
- **Tenant:** `Tenant.Language` (columna `tenancy.tenants.language`, `varchar(10) NOT NULL DEFAULT 'es-419'`). Lo editan
  `GET`/`PUT /tenants/{id}/settings` en el campo `language`. Si el `PUT` no trae el campo, conserva lo guardado (ventana de
  despliegue, como `country`). Un valor fuera del catálogo responde `422 tenancy.settings.language.invalid`. El cambio sube la versión
  y entra en `changedFields` como `language`.
- **Persona:** `UserPreference.Language` (columna `identity.user_preferences.language`, `varchar(10)` nullable; `null` = «el del
  tenant»). Se guarda el `null` y no una copia, para que un cambio del tenant alcance a quien nunca eligió.
  - **`PUT /auth/preferences`:** `language` ausente o `null` conserva lo guardado; `""` vuelve al idioma del tenant; `es-419` o
    `es-ES` lo fijan. Un valor fuera del catálogo responde `422 identity.preference.language.invalid`, y un cuerpo sin ninguno de los
    cuatro campos, `422 identity.preference.empty`.
  - **`GET` y `PUT`:** devuelven `language` (lo guardado, que es lo que edita el selector) y `effectiveLanguage` (el diccionario que la
    pantalla tiene que cargar).
- **Sesión:** `/auth/me` y `POST /auth/session` publican en cada `activeTenants[]` el `language` **efectivo**. La SPA elige el
  diccionario antes de la primera pintura, y `cashier` no puede leer `/settings`. Hay una sola lectura de preferencias por sesión
  (`IUserPreferenceService.GetLanguagesAsync`), no una por tenant.
- **Correos:** el español de los correos ya es neutro y tutea, y sirve en los dos idiomas. Solo cambian los términos de D-I5:
  - Exportación de cotizaciones lista o fallida (`QuotationsExportKindText`): la recibe quien la pidió, en su idioma efectivo
    (`OutboxDeliveryWorker.ResolveLanguageAsync`).
  - Apertura de un ticket de Soporte (`SupportOpeningEmailTemplate`): la recibe un cliente sin preferencia propia, en el idioma del
    tenant (`SupportOpeningSender`). El parámetro `radicado` del enlace de consulta es contrato con el formulario público y **no** se
    traduce.

- **Registro:** `POST /auth/register-tenant` y `POST /auth/password/register-tenant` aceptan `language` opcional. Ausente o `""` es
  `es-419`, y un valor fuera del catálogo responde `422 tenancy.settings.language.invalid` antes de crear nada (`EnsureValidRegistration`).
- **PDF de cotización y de pedido** (`quotation.typ`): sale en el idioma **del tenant**, no del que descarga, porque el documento es
  del tenant. En `es-ES` la cotización es «Presupuesto», masculino («Emitido», «Válido hasta»), y el rótulo «Asesor» pasa a
  «Comercial», también en el pedido. El idioma viaja en `data.language`.
  - **Regeneración:** el PDF guardado se vence por la huella (`QuotationPdfSectionsBuilder.Fingerprint`), sin columna nueva. Solo
    entra a la huella un idioma distinto de `es-419`, así que **el despliegue no regenera ningún PDF** de los tenants de hoy y pasar un
    tenant a `es-ES` regenera los suyos en el siguiente export.
  - **Nombre del archivo:** una sola regla, `QuotationPdfFileName`: `Presupuesto-N.pdf` en `es-ES`. La usan el renderer, la descarga y
    el adjunto que llega por WhatsApp (`WhatsAppQuotationMessage.Language`). El texto de la plantilla de WhatsApp no cambia.
- **Excel de cotizaciones** (`QuotationsExportProcessor`): en `es-ES` la hoja es «Presupuestos», el archivo `presupuestos-….xlsx` y la
  columna «Asesor» se llama «Comercial». Va en el idioma del tenant. El correo de aviso va en el de quien exportó, así que una persona
  en `es-ES` dentro de un tenant `es-419` recibe «Tu exportación de presupuestos» con el archivo `cotizaciones-….xlsx`. Es aceptable:
  el archivo es de la organización. **La hoja del ERP (Excel de pedidos) no se toca** (D-R8).

## Despliegue

Las dos migraciones son **aditivas** (`AddTenantLanguage`, `AddUserPreferenceLanguage`). Un pod viejo que inserta un tenant deja
`es-419` por el `DEFAULT` de la columna, y uno que guarda una preferencia deja `language` en `NULL`. Los campos nuevos de las
respuestas no rompen a un frontend viejo, y los del cuerpo son opcionales.

## Pendiente (no se asume)

- **Textos del backend que siguen en `es-419`:** plantillas de WhatsApp (Meta las aprueba por idioma), nombres y descripciones de
  roles y permisos (el rol de sistema «Asesor», `/authorization/catalog`), historial de cambios, mensajes del bot, variables del PDF
  (`{asesor}` en `QuotationPdfVariables`) y el `detail` de `ApiExceptionHandler`. El owner decidió no tocar roles ni permisos en esta
  entrega.
- **Correos sin tenant** (restablecer contraseña, verificar correo, comprador): salen en `es-419`. Su texto ya es igual en los dos
  idiomas, así que hoy no hay diferencia; habrá que decidirlo cuando la haya.
