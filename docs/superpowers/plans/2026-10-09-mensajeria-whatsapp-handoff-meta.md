# HANDOFF — lo que el owner hace en Meta para la mensajería por WhatsApp

Spec: `docs/superpowers/specs/2026-10-09-mensajeria-whatsapp-design.md` (§14). Nada de esto lo puede hacer el
backend; sin estos pasos el flujo de conexión no abre y el webhook no recibe.

## Lista de pasos

1. **App en Live.** *App Dashboard → App settings → Basic*: cambia el interruptor a «Live» (exige política de
   privacidad y verificación del negocio) y copia el **App ID** y el **App Secret** (botón «Show»).
   Valores: App ID → variable normal `META_APP_ID`; App Secret → variable **secreta** `META_APP_SECRET`.
2. **Permisos con Advanced Access.** *App Review → Permissions and Features*: solicita `whatsapp_business_management`
   y `whatsapp_business_messaging`. `business_management` **no** se pide (D-M19): sólo lo exige Meta a un
   Solution Partner que comparte línea de crédito.
3. **Dominios.** *App Dashboard → App settings → Basic → App Domains*, y en *Facebook Login for Business →
   Settings*: `qep.qcode.co` en «Allowed Domains for the JavaScript SDK» y en «Valid OAuth Redirect URIs»
   (valor: `https://qep.qcode.co/`).
4. **Configuración de Embedded Signup.** *Facebook Login for Business → Configurations → Create configuration*:
   plantilla **WhatsApp Embedded Signup v4**, con los eventos `FINISH` y los de coexistencia. Copia el
   **Configuration ID** → variable normal `META_CONFIG_ID` (ConfigMap `Meta__App__ConfigId`). El App ID del paso
   1 va en `Meta__App__AppId`; la versión de Graph queda en `Meta__App__GraphApiVersion: "v24.0"`.
5. **Webhook.** *WhatsApp → Configuration → Webhook → Edit*: «Callback URL» =
   `https://<host de la API>/api/webhooks/whatsapp`; «Verify token» = el mismo valor que la variable secreta
   `META_WEBHOOK_VERIFY_TOKEN` (aleatorio, 32+ caracteres). Luego «Manage» y suscribe `messages`, `account_update` y, si
   la app lo ofrece en la lista de campos, `user_id_update` (spec 2026-10-10 §13: la documentación no muestra
   si es un campo aparte). Después del primer cambio de número real, revisa en el log que llegó y que la
   conversación siguió siendo la misma; hasta entonces el cambio de número se considera no verificado.
   Desde el gestor de plantillas (spec 2026-10-14) suscribe también `message_template_status_update`: sin él, las
   aprobaciones y los rechazos sólo se ven al refrescar la pantalla de plantillas.
6. **Secretos en `Backend-prod`** (Azure DevOps → Pipelines → Library): variables secretas `META_APP_SECRET` y
   `META_WEBHOOK_VERIFY_TOKEN`; variables normales `META_APP_ID` y `META_CONFIG_ID`. Van **antes** del deploy:
   sin las cinco claves el pod no arranca (`MetaAppOptionsValidator`, a propósito).
7. **Orden.** Despliega el backend **antes** de guardar el webhook en Meta (hace el `GET` de verificación al
   guardarlo), y el frontend después.
8. **Prender `messaging` por tenant.** Desde la consola de operador, o con el SQL de respaldo del README
   («Módulos por tenant»). No viene con el signup.
9. **Coexistencia (D-M13, D-M15):** lo que alguien responda desde la app de WhatsApp Business en el teléfono
   **no aparece en QEP** en esta versión: no se suscriben `history`, `smb_app_state_sync` ni
   `smb_message_echoes` (el campo `smb_message_echoes` **no** va en los campos del webhook; entra con un slice
   posterior). Y si la cuenta de WhatsApp Business tiene varios números sin conectar, el flujo de coexistencia
   no puede saber cuál se eligió y responde `integrations.whatsapp.registration_failed`: se conecta un número
   por vez, o se usa el camino de número nuevo.
10. **PIN de dos pasos** (actualizado el 2026-10-14): si el número ya tiene verificación en dos pasos,
    escribe su PIN en el formulario del camino de número nuevo; si no, déjalo vacío y QEP genera uno. En
    los dos casos queda cifrado en la conexión. Un PIN equivocado vuelve marcado en el campo
    (`registration_failed` con `errors.pin`); diez registros en 72 h devuelven
    `registration_limit` y hay que esperar. Si el número ya está `CONNECTED` en la Cloud API (por ejemplo, con
    otra app tuya), QEP no lo vuelve a registrar: sólo suscribe la app, ignora el PIN y no lo guarda.
11. **Orden del despliegue de la asignación y el BSUID** (spec 2026-10-10 §7, §13). Tres pasos, en este orden:
    1. **Backend**, con sus dos migraciones: `AddCustomerCompleteness` (Customers) y
       `AddBsuidAssignmentAndEvents` (Messaging).
    2. **Frontend de este slice.** El SPA viejo que sigue vivo espera `classification` y `cuc` no nulos en el
       cliente, trata `direction: "System"` como error de contrato y dibuja `+{waId}` aunque venga `null`. No
       se rompe de inmediato: los clientes incompletos y los eventos sólo aparecen cuando la ingesta corre.
    3. **Sólo entonces** prende una conexión de WhatsApp en producción (paso 8 y la conexión desde la SPA).
       Prenderla antes deja entrar incompletos que el frontend viejo no sabe dibujar.

    `AddBsuidAssignmentAndEvents` tiene que salir **antes o junto con** el primer despliegue de Messaging a
    producción: sus `CHECK` recorren `messaging.messages`, y eso sólo es barato mientras la tabla está vacía.

## Notas a confirmar en producción

- **Formato del `sha256` de un medio.** Confirma contra **una respuesta real** de `GET /{media-id}` si el
  campo `sha256` llega en hex de 64 caracteres o en base64 de 44. La copia acepta los dos
  (`MediaTransfer.Sha256Matches`); si llega en otro formato, el medio se guarda **sin verificar** y queda un
  warning `stored without verification` por fila en el log. Míralo en el primer medio real de producción.
- **Copia a R2 por stream.** Confirma con un medio real (imagen y un documento grande) que el `PUT` en
  streaming a R2 funciona y que el SHA-256 coincide; hasta hoy sólo se ejercitó con dobles de prueba.
- **Redacción del query en OpenTelemetry.** El canje del `code` de Embedded Signup lleva `client_secret` y
  `code` en el query de la URL. La instrumentación de `HttpClient` de OpenTelemetry .NET redacta por defecto
  los valores del query en `url.full` (desde la 1.7; aparecen como `Redacted`). Nunca pongas
  `OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION` en `true`. Comprobación: abre en Tempo una
  traza de un Embedded Signup y confirma que `url.full` muestra `client_secret=Redacted&code=Redacted`.
- **Plantillas rechazadas.** Precedente de Zenvia: un rechazo con «An error occurred while sending the
  template for approval» lo produce el agregador, no Meta. Con la Cloud API no hay agregador de por medio: las
  plantillas que crea el gestor (spec 2026-10-14) las revisa Meta, y su motivo llega en `rejected_reason`.

- **Número ya registrado.** El signup no llama `/register` si `GET /{phoneNumberId}` devuelve `status = CONNECTED` y
  `platform_type = CLOUD_API`. La documentación de Meta sólo muestra `CLOUD_API` en un ejemplo y no lista los otros
  valores: confirma con el primer número **nuevo** que conectes que antes del `/register` no aparece así (si apareciera,
  QEP no lo registraría y el número no recibiría). La auditoría de la conexión lleva `register:skipped` cuando se saltó.

**Gestor de plantillas** (spec 2026-10-14, §3). Todo esto se ejercitó sólo contra dobles de prueba:

- **Token de la Resumable Upload.** El aprovisionamiento de la plantilla de cotización sube el PDF de ejemplo a
  `/{AppId}/uploads` con el **token de negocio de la conexión**. La documentación lo ejemplifica con un token de
  usuario. Si el primer aprovisionamiento real responde `messaging.template.upload_failed`, mira el log de
  `template-upload-start` (status y `code`) antes de cambiar nada; la alternativa es un token de la app.
- **Formato del id de la subida.** QEP acepta `upload:…` con o sin firma (`upload:…?sig=…`), que es la forma que se
  vio en la práctica, y manda la `?sig=` como query del segundo request (`template-upload-data`). Un id con otra forma
  corta la subida con `upload_failed` aunque `template-upload-start` haya respondido 200 (falla interna
  `invalid_upload_id`). Confirma en el primer aprovisionamiento real que el segundo request responde con
  `{ "h": … }`.
- **Cuerpo exacto del error de nombre en cuarentena.** Meta bloquea un tiempo el nombre de una plantilla borrada. QEP
  trata como «el nombre ya existe» (`name_taken`) un 400 cuyo `error_user_title` o `error_user_msg` diga
  «already exist» —el que usan las pruebas es `{"error":{"message":"(#100) Invalid parameter","code":100,
  "error_user_title":"Message Template Name Already Exists",…}}`— y con eso el reintento de la plantilla de cotización
  salta a la versión siguiente. **No se ha visto el cuerpo real de un nombre en cuarentena.** La primera vez que
  aparezca (un reintento, o una libre recreada con el nombre de una borrada), anota el cuerpo completo: si no dice
  «already exist», cae en `rejected_by_meta` con el `error_user_msg` de Meta, el reintento no salta de versión y
  hay que ajustar `TemplateGraphErrors`.
- **Textos de `error_user_title`.** Igual para el tope de plantillas («limit»). Si un rechazo real llega con otro
  título, cae en `rejected_by_meta`; anota el título para ajustar `TemplateGraphErrors`.
- **`rejected_reason` en `fields`.** El refresco pide `id,name,language,status,category,components,parameter_format,
  rejected_reason` al listar. Confirma con una plantilla rechazada real que el motivo llega en el listado. Si Meta
  rechazara el campo, el listado entero fallaría con `messaging.templates.unavailable` y habría que quitarlo de
  `TemplateFields`; si lo acepta pero no lo llena, el espejo sólo sabría el motivo por el webhook.

## Decisiones a ratificar (spec §13)

Si alguna está mal, el costo de cambiarla está en la tabla del spec.

- **D-M13:** los campos de coexistencia (`history`, `smb_app_state_sync`, `smb_message_echoes`) no se suscriben;
  las respuestas desde la app del teléfono no aparecen en QEP.
- **D-M14:** el probador acepta el número si `GET /{phoneNumberId}` responde 200, sin mirar `status`.
- **D-M15:** coexistencia con varios números sin conectar: no se elige ninguno, responde 422
  `integrations.whatsapp.registration_failed`.
- **D-M16:** cuenta deshabilitada, borrada, app desinstalada o partner quitado → `lastFailureCode`
  `account_disabled` (el frontend aún lo muestra crudo).
- **D-M17:** `connectionName` de una conexión eliminada es `"Conexión eliminada"`.
- **D-M18:** módulo `messaging` apagado con mensajes llegando: los entrantes se descartan; los `statuses` sí se
  aplican.
- **D-M19:** `business_management` no se pide en App Review.
- **D-M20:** `sentBy.displayName` nunca es `null`; si la membresía ya no está o no tiene nombre ni correo,
  viaja `"Miembro eliminado"`.
