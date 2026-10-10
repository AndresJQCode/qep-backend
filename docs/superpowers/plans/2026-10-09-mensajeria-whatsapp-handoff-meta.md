# HANDOFF — lo que el owner hace en Meta para la mensajería por WhatsApp

Spec: `docs/superpowers/specs/2026-10-09-mensajeria-whatsapp-design.md` (§14). Nada de esto lo puede hacer el
backend; sin estos pasos el flujo de conexión no abre y el webhook no recibe.

## Lista de pasos

1. **App en Live**, con verificación del negocio y App Review aprobados.
2. **Permisos con Advanced Access:** `whatsapp_business_management` y `whatsapp_business_messaging`.
   `business_management` **no** se pide (D-M19): sólo lo exige Meta a un Solution Partner que comparte línea
   de crédito.
3. **Dominios:** `qep.qcode.co` en «Allowed Domains for the JavaScript SDK» y en «Valid OAuth Redirect URIs».
4. **Configuración de Facebook Login for Business** para **WhatsApp Embedded Signup v4** (con los eventos
   `FINISH` y los de coexistencia); su id va a la variable `META_CONFIG_ID` (ConfigMap `Meta__App__ConfigId`).
   El id de la app va a `META_APP_ID` (`Meta__App__AppId`). La versión de Graph queda en
   `Meta__App__GraphApiVersion: "v24.0"`.
5. **Webhook:** URL `https://<host de la API>/api/webhooks/whatsapp`, token de verificación = el mismo valor
   que la variable secreta `META_WEBHOOK_VERIFY_TOKEN`, y **sólo** los campos `messages` y `account_update`.
6. **Secretos en `Backend-prod`:** variables secretas `META_APP_SECRET` y `META_WEBHOOK_VERIFY_TOKEN` (token
   aleatorio de 32+ caracteres), y variables normales `META_APP_ID` y `META_CONFIG_ID`. Van **antes** del
   deploy: sin las cinco claves el pod no arranca (`MetaAppOptionsValidator`, a propósito).
7. **Orden:** desplegar el backend **antes** de guardar el webhook en Meta (hace el `GET` de verificación al
   guardarlo), y el frontend después.
8. **Prender `messaging` por tenant:** desde la consola de operador, o con el SQL de respaldo del README
   («Módulos por tenant»). No viene con el signup.
9. **Coexistencia (D-M13, D-M15):** lo que alguien responda desde la app de WhatsApp Business en el teléfono
   **no aparece en QEP** en esta versión: no se suscriben `history`, `smb_app_state_sync` ni
   `smb_message_echoes` (el campo `smb_message_echoes` **no** va en los campos del webhook; entra con un slice
   posterior). Y si la cuenta de WhatsApp Business tiene varios números sin conectar, el flujo de coexistencia
   no puede saber cuál se eligió y responde `integrations.whatsapp.registration_failed`: se conecta un número
   por vez, o se usa el camino de número nuevo.
10. **PIN de dos pasos:** el registro fija un PIN aleatorio que no se guarda. Si el número ya tenía
    verificación en dos pasos con otro PIN, `/register` falla (`registration_failed`): quita ese PIN en
    WhatsApp Manager y reintenta.

## Notas a confirmar en producción

- **Formato del `sha256` de un medio.** Confirma contra **una respuesta real** de `GET /{media-id}` si el
  campo `sha256` llega en hex de 64 caracteres o en base64 de 44. La copia acepta los dos
  (`MediaTransfer.Sha256Matches`); si llega en otro formato, el medio se guarda **sin verificar** y queda un
  warning `stored without verification` por fila en el log. Míralo en el primer medio real de producción.
- **Copia a R2 por stream.** Confirma con un medio real (imagen y un documento grande) que el `PUT` en
  streaming a R2 funciona y que el SHA-256 coincide; hasta hoy sólo se ejercitó con dobles de prueba.
- **Redacción del query en OpenTelemetry.** El canje del `code` de Embedded Signup lleva `client_secret` y
  `code` en el query de la URL. `AddHttpClientInstrumentation` depende de la redacción por defecto del query:
  no la desactives nunca (`OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION`) y, si cambias de
  versión de OpenTelemetry, verifica en Tempo que el atributo `url.full` siga mostrando `Redacted`.
- **Plantillas rechazadas.** Precedente de Zenvia: un rechazo con «An error occurred while sending the
  template for approval» lo produce el agregador, no Meta. En esta versión no se envían plantillas (sólo texto
  dentro de la ventana de 24 h), pero si se agregan, aplica lo mismo.

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
