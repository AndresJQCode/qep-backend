# Reporte de uso de la plataforma

Fecha: 2026-11-01. Repos: `qep-backend` y `qep-frontend`.

## Por qué

El dueño de la plataforma quiere conocer el crecimiento y el uso de QEP sin pedir una consulta a la base de producción.
Hoy eso sólo se puede saber con SQL a mano.

## Objetivos

- Ver cuántos tenants se registraron hoy, esta semana y este mes.
- Ver, por tenant, cuántas conversaciones hubo en el período elegido, separadas por canal.
- Ver quién está conectado ahora, agrupado por tenant.
- Que sólo lo vean usuarios del tenant operador con el permiso del reporte.

## Fuera de alcance

- Historial de conexiones, «última vez visto» y tiempo conectado.
- Rangos de fecha personalizados, gráficas y exportación.
- Conexión de usuarios fuera de mensajería.
- Presencia compartida entre réplicas.

## Qué no debe pasar

- Que un tenant cliente vea el reporte o sepa que existe.
- Que el historial importado infle el mes en que llegó.
- Que el contador de uso deje lenta la bandeja de mensajería.

## La pantalla

Pestaña nueva «Uso» en `OperatorConsoleTabs`, junto a Tenants y Ciudades, en `/operator/usage`. Se dibuja sólo con su permiso,
como «Ciudades». Es una sola página.

1. Tres totales arriba, siempre visibles: tenants registrados hoy, esta semana y este mes. El selector no los cambia.
2. Selector de período para la tabla: Hoy, Semana y Mes. Por defecto, Mes.
3. Una tabla con una fila por tenant: nombre, WhatsApp, Instagram, Total y Conectados.
4. Una fila se despliega y muestra los nombres de sus conectados. Sin conectados no se despliega.

Salen todos los tenants, también los que están en cero. El orden es por total descendente y luego por nombre. El tenant operador
no sale en la tabla ni en los totales, para no mezclar sus pruebas con el uso de los clientes. Los tenants suspendidos sí salen,
porque se registraron y pudieron tener uso en el período.

## Definiciones

- **Tenant registrado:** por `Tenant.CreatedAt`.
- **Períodos:** hora de Colombia (`America/Bogota`) para todos los tenants. Hoy es desde la medianoche, la semana empieza el lunes y
  el mes el día 1. Todos terminan en este instante. La zona propia de cada tenant no se usa, porque los totales tienen que sumar sobre
  una sola línea de tiempo.
- **Conversación del período:** una conversación distinta en la que el cliente escribió al menos un mensaje entrante en el rango (decisión
  del owner). Una conversación vieja en la que el cliente volvió a escribir cuenta. Una conversación cuenta una vez aunque tenga muchos mensajes.
- **Qué no cuenta:** nada saliente: campañas, plantillas automáticas, envíos por API, ecos de coexistencia, respuestas de asesores o del bot.
  Una campaña sin respuesta no suma. Tampoco cuentan los mensajes con `imported = true` ni las filas de eventos.
- **Canal:** `ConversationChannel`. La respuesta trae los dos canales siempre, también en cero. Un canal nuevo agrega un campo.
- **Conectado:** un miembro activo con al menos una conexión abierta a `MessagingHub`. El frontend abre esa conexión en la ruta
  `_authenticated/messaging.tsx`, así que significa «tiene la bandeja abierta». Varias pestañas cuentan una sola vez.

## Acceso

Permiso nuevo `operator.usage.read`, con sus dos mitades: la constante y su política en `AddAuthorization`. Va en el rol de sistema
`admin`, junto a los demás `operator.*`, y su entrada en el catálogo de permisos con categoría `Operator`. `OperatorPermissionFilter` lo
quita fuera del tenant operador. El handler revalida con `OperatorAuthorization.EnsureAuthorized`: 403 si el tenant de la ruta no es el
activo, si falta el permiso o si no es el operador.

Los `operator.*` hoy viven sólo en roles de sistema (decisión D11 del spec 2026-10-08), así que el acceso lo tienen los `admin` de QCode.

## Contrato

`GET /api/v1/tenants/{tenantId:guid}/operator/usage?period=day|week|month`

```json
{
  "registered": { "day": 2, "week": 5, "month": 11 },
  "period": "month",
  "tenants": [
    {
      "tenantId": "…",
      "name": "Clasic",
      "conversations": { "whatsApp": 1240, "instagram": 310, "total": 1550 },
      "connected": [{ "memberId": "…", "name": "Ana Ruiz" }]
    }
  ]
}
```

Un `period` inválido es 422 `validation.failed` con el mapa `errors`. La tabla no se pagina: los tenants son pocos y el orden depende del
total. La pantalla refresca al volver a la pestaña y con un botón. No hay actualización en vivo.

## Presencia

Nadie lleva hoy esta cuenta: `MessagingHub` no implementa `OnDisconnectedAsync`. Se agrega un registro en memoria, singleton y seguro
para concurrencia, de conexiones por `(tenantId, memberId)`.

- Se registra al final de `OnConnectedAsync`, sólo si la conexión pasó todas las validaciones.
- Se retira en `OnDisconnectedAsync`, que debe tolerar una conexión que nunca se registró, porque `Context.Abort()` también lo dispara.
- Un miembro sale de la lista cuando se cierra su última conexión.
- Un reinicio vacía el registro. Los clientes reconectan solos, así que se repuebla en segundos.

Con `replicas: 1` (`k8s/prod-deployment.yaml`) esto es correcto. Con dos réplicas cada pod vería sólo a los suyos. Se documenta en el
`CLAUDE.md` del repo como condición para subir réplicas, y el puerto se define de forma que el cambio a un almacén compartido no toque
al handler.

## Forma técnica

Sigue el patrón de `OrdersReportSource`: lectura por nombre en SQL crudo en Bootstrapper sobre `tenancy.tenants`, `messaging.messages` y
`messaging.conversations`, porque son `DbContext` distintos. El endpoint, el query y su handler siguen el patrón de la consola
(`OperatorEndpoints`, doble capa). Los nombres de los conectados se resuelven con los directorios existentes, en una sola consulta.

La consulta del mes debe usar un índice existente. `IX_messages_tenant_created` cubre `tenant_id, created_at` e incluye `conversation_id` y `direction`.
Por eso el rango se aplica sobre `created_at`, que para lo no importado es la hora en que entró a QEP. El índice no incluye `imported`, así
que cada fila va a la tabla. Esta función no agrega índices (decisión del diseñador): un índice más encarece cada mensaje de la bandeja, y este
reporte se abre pocas veces. Se mide con `EXPLAIN ANALYZE` en la base local. Si el mes supera 2 segundos, se reporta al owner en vez de
agregar el índice por cuenta propia.

La lectura por nombre queda amarrada por una prueba de integración contra el esquema migrado.

## Pruebas

- Handler: 403 en los tres casos de `OperatorAuthorization`, y 422 por período inválido.
- Fuente: bordes de día, semana y mes en Colombia; importados excluidos; eventos excluidos; salientes sin entrante no cuentan; conversación con varios
  entrantes cuenta una; conversación vieja con entrante nuevo cuenta; tenant en cero sale; operador excluido; dos canales.
- Presencia: dos conexiones del mismo miembro cuentan una, cerrar una lo deja, cerrar la última lo saca, y cerrar una no registrada no falla.
- Hub: una conexión rechazada no queda registrada.
- Frontend: la pestaña depende del permiso, el selector cambia la tabla y no los totales, la fila se despliega y el estado vacío se ve.

## Despliegue

Primero el backend y después el frontend. No hay migración de esquema. El permiso nuevo llega al rol `admin` por el mecanismo existente de
los roles de sistema; hay que verificarlo en el plan, y recordar que un permiso nuevo exige reiniciar y recargar la sesión.

## Decisiones del implementador

- Nombres de clases, puerto de presencia y dónde vive cada pieza, respetando `ArchitectureTests`.
- Cómo identificar en SQL las filas de evento de `messaging.messages`.
- Copia de textos de la pantalla, en español con tuteo.
