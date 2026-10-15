# HANDOFF — lo que el owner hace en Meta para la mensajería por Instagram

Spec: `docs/superpowers/specs/2026-10-14-mensajeria-instagram-design.md`. El backend de la rama
`claude/instagram-integration-plan-8x9vcm` ya trae el proveedor, el webhook, la ingesta, el envío y el refresco del
token; nada de lo de abajo lo puede hacer el código, y sin estos pasos la conexión no abre y el webhook no recibe.

## Ya hecho por el owner (2026-10-14)

- El caso de uso **Instagram** se agregó a la app de Meta que ya tiene WhatsApp. Tiene su propio **Instagram App ID**
  y **App Secret**, distintos de los de la app (V-1 cerrada).
- Quedaron registrados los dos redirect URI: `https://qep.qcode.co/settings/integrations/instagram/callback` y
  `http://localhost:3002/settings/integrations/instagram/callback` (V-6 cerrada).

## Lista de pasos

1. **Variables del pipeline** (`Backend-prod`): `META_INSTAGRAM_APP_ID` como variable normal y
   `META_INSTAGRAM_APP_SECRET` como variable **secreta**. Van **antes** del deploy: en `Production`,
   `MetaInstagramOptionsValidator` exige las dos (y `Meta:Instagram:AllowedRedirectOrigins`, que ya está en el
   ConfigMap). Nunca pegues el secreto en un chat, un log ni una captura.
2. **Permisos.** *App Review*: `instagram_business_basic` e `instagram_business_manage_messages` con acceso avanzado.
   Con la app en modo desarrollo sólo se puede escribir a personas con rol en la app: sirve para probar, no para abrir
   al público.
3. **Webhook.** *Instagram → Configuración → Webhooks*: «Callback URL» = `https://<host de la API>/api/webhooks/instagram`;
   «Verify token» = el mismo valor de `META_WEBHOOK_VERIFY_TOKEN` (se reutiliza el de WhatsApp). Suscribe
   `messages`, `messaging_seen`, `messaging_postbacks` y `messaging_reactions`. Despliega el backend **antes** de
   guardar: Meta hace el `GET` de verificación al guardar.
4. **Prender `messaging` por tenant** si todavía no está (consola de operador o el SQL de respaldo del README).
5. **Conectar la primera cuenta** desde *Configuración → Integraciones → Instagram (Meta) → Conectar*: entra con el
   Instagram profesional (business o creator) y acepta los dos permisos. La conexión tiene que quedar `Active` con el
   `@usuario`. Si Meta pide activar «Permitir acceso a mensajes» en la configuración del Instagram (*Controles de
   mensajes → Herramientas conectadas*), anótalo: es la V-2 y hay que agregar ese texto a la pantalla.

## Qué verificar con la primera cuenta real (lo que ninguna prueba automática pudo cerrar)

| Id | Qué probar | Qué esperar | Si sale distinto |
| --- | --- | --- | --- |
| V-2 | Escribirle un DM a la cuenta desde otro Instagram | La conversación aparece en la bandeja con el ícono de Instagram, nombre y `@usuario` | Si no llega nada: revisa en el log del pod si el webhook recibió (`provider = 'instagram'` en `messaging.webhook_deliveries`); si no recibió, falta la suscripción de la cuenta (`subscribed_apps`) o el acceso a mensajes de la cuenta |
| V-3 | Responder con un texto de 1000 caracteres | Sale. Con 1001 el formulario marca el campo | Si Meta rechaza menos de 1000, se baja `SendMessageValidator.InstagramTextMaxLength` |
| V-4 | Responder con una imagen y con un PDF | Llegan al Instagram de la persona | Si Meta no descarga la URL prefirmada de R2 (el envío queda `Failed`), hay que publicar una copia pública con clave aleatoria, como el PDF de las cotizaciones |
| V-5 | Abrir la conversación en QEP | La persona ve «Visto» en su Instagram | Si Meta responde error al `mark_seen`, queda en el log como warning y no pasa nada más |
| V-7 | La conversación nueva | Trae nombre y `@usuario` | Si Meta no da `username`, el cliente nace con el nombre o con «Contacto de Instagram» |
| V-8 | Mandar una nota de voz | Se oye como audio en el Instagram de la persona y se entiende bien | Si Meta la rechaza, el envío queda `Failed`; la alternativa es `ffmpeg` en la imagen y AAC (D-I10) |
| — | Que la persona reaccione, comparta una publicación y borre un mensaje | Reacción con su emoji, un ítem «Compartido» con el enlace, y el mensaje tachado | Cualquier forma que no sea la esperada queda como `Unsupported` o se ignora, con una línea en el log |

Cuando esto esté verificado, se actualiza el spec (§3, tabla «A verificar») y este archivo.
