# Envío de cotizaciones por el canal de WhatsApp del tenant (Meta o Zenvia), con Zenvia habilitado por el operador

**Fecha:** 2026-10-21
**Módulos:** Quotations (Application, Infrastructure, Api), Integrations (Domain, Application, Infrastructure, Api),
Messaging (sólo el puerto de envío de la plantilla de cotización), Tenancy (consola de operador), Bootstrapper;
frontend `features/operator`, `features/integrations`, `features/quotes`, `features/tenant-settings`.
**Estado:** borrador para revisión del owner.
**Es el «consumidor Quotations»** que dejaron pendiente el spec de integraciones (2026-10-08, «Fuera de alcance») y
el del gestor de plantillas (2026-10-14, §5).

## 1. Problema

Hoy «Enviar por WhatsApp» de una cotización sale **siempre** por una única cuenta global de Zenvia
(`Quotations:WhatsApp:{ApiToken, FromNumber, TemplateId}`), registrada como `ZenviaWhatsAppSender` singleton en
`QuotationsInfrastructureExtensions.AddWhatsAppSender`. Esa cuenta es del tenant Origen Botánico (spec 2026-10-08,
DECISIÓN-PENDIENTE 2), así que todos los tenants envían desde el número de otro.

A la vez ya existe, sin usarse para cotizaciones:

- La conexión de **Zenvia** por tenant en Integraciones (`IntegrationProviders.Zenvia`, consumidor `quotations`):
  se crea, se prueba, pero ningún envío la lee.
- La conexión de **WhatsApp Business (Meta)** (`whatsapp-cloud`, consumidor `messaging`) con la plantilla de
  cotización aprovisionada por QEP y la señal `IQuotationTemplateReadiness.FindApprovedAsync` (spec 2026-10-14 §4.3,
  §4.5): dice si una conexión puede enviar la cotización y con qué nombre e idioma de plantilla.

Y no existe forma de que el operador de la plataforma decida qué proveedores puede usar cada tenant: el catálogo de
Integraciones sólo filtra por módulo (`IntegrationProvider.IsVisibleFor`).

## 2. Objetivo

1. Cada tenant envía la cotización por **su propia** conexión de WhatsApp: Meta o Zenvia. Se retira la cuenta global.
2. **Zenvia es un proveedor que el operador habilita o deshabilita por tenant** desde la consola de Plataforma.
   Deshabilitado por defecto en los tenants nuevos.
3. Con más de una conexión disponible, el tenant elige por cuál sale. Sin ninguna, no se puede enviar y la
   pantalla lo dice antes de intentarlo.

## 3. Decisiones

Las marcadas **(owner)** las tomó el owner en la conversación del 2026-10-21. Las marcadas **(propuesta)** son del
autor del spec y el owner las aprobó junto con el resto del documento.

| # | Decisión | Origen |
|---|---|---|
| D1 | No hay cuenta global. Cada tenant configura sus credenciales de Zenvia. Se retiran `Quotations:WhatsApp:*`, `ZenviaWhatsAppSender` global y `LogWhatsAppSender` como default. | owner |
| D2 | Zenvia se habilita **por tenant** y lo decide el operador de la plataforma. Los tenants nuevos nacen con Zenvia deshabilitado. | owner |
| D3 | No es un módulo comercial: es un **proveedor habilitado por tenant**. No entra en `TenantModuleKeys`. | owner |
| D4 | Un canal **disponible** es una conexión que puede enviar ahora (definición exacta en §4.1). | propuesta |
| D5 | Una sola conexión disponible → se usa, sin configurar nada. | owner |
| D6 | Más de una conexión disponible → se usa la **elegida por el tenant**. Si no hay ninguna elegida, al enviar se abre un diálogo para elegirla y la elección **se guarda** como la configuración del tenant. | owner |
| D7 | Se elige la **conexión** (el número), no sólo el proveedor: una conexión es lo que ve el cliente final, y resuelve también varias conexiones del mismo proveedor (hasta 5 de Meta y 20 de Zenvia). | propuesta |
| D8 | La elección desde el diálogo de envío la puede guardar **cualquiera que pueda enviar** (`quotations.quotation.manage`), aunque no tenga `tenancy.settings.update`. Sólo se puede hacer mientras no haya una elegida. Cambiarla después, desde Configuración, exige `tenancy.settings.update`. | owner |
| D9 | Ninguna conexión disponible → el botón «Enviar por WhatsApp» aparece deshabilitado con su explicación, y el backend responde 422 con código propio si lo llaman igual. La cotización sigue en borrador. | owner |
| D10 | Si la conexión elegida deja de estar disponible (se borra, se pausa, su plantilla de Meta deja de estar aprobada, el operador deshabilita Zenvia), se trata como «no elegida»: con una sola disponible se usa esa; con varias, vuelve el diálogo. La elección guardada se reemplaza al elegir de nuevo. | propuesta |
| D11 | El ID de la plantilla de Zenvia pasa a ser un **campo de la conexión de Zenvia** (`templateId`, requerido), junto al token y el número. Revisa la D6 del spec 2026-10-08, que la dejaba fuera de la conexión «salvo que el consumidor la quiera por conexión»: en Zenvia la plantilla va atada al número emisor. | propuesta |
| D12 | La habilitación por tenant es **genérica** («proveedores habilitados por tenant»): un proveedor del catálogo declara si la necesita. Hoy sólo Zenvia la necesita. | propuesta |
| D13 | Cambiar la habilitación pide confirmación, **motivo y nota**, con las mismas listas de motivos que los módulos (`MODULE_REASONS`: activar = `contract`, `courtesy`, `correction`; desactivar = `nonpayment`, `cancellation`, `correction`), y queda en el historial del tenant de la consola. | propuesta |
| D14 | Tenants que ya existen: Zenvia queda **habilitado** en los que tengan al menos una conexión de Zenvia (en cualquier estado) y deshabilitado en el resto. Nadie pierde lo que ya configuró. | propuesta |
| D15 | Deshabilitar Zenvia **no borra** sus conexiones. Quedan visibles en Integraciones como «Deshabilitado por la plataforma», sin poder crear, editar, probar ni reanudar; sí se pueden borrar. No cuentan como canal disponible. Rehabilitarlo devuelve todo como estaba. | propuesta |
| D16 | Una cotización enviada por Meta **queda en el hilo** de Mensajería de ese número en esa conexión, así que la respuesta del cliente llega con contexto. Con más de una conexión disponible, sale por la elegida (D6, D7). | owner |
| D16a | El envío de la cotización **no pasa por las guardas de la bandeja** (`OutboundGuards`): lo autoriza `quotations.quotation.manage`, no exige permisos de Mensajería al que envía, no se rechaza si la conversación está resuelta o asignada a otra persona, y **no cambia** su estado ni su asignación. Una conversación nueva nace `Open` y sin asignar. La respuesta del cliente sigue las reglas de entrada de siempre (reabre, notifica). | propuesta |
| D16b | La conversación se busca por el **teléfono de destino** en esa conexión. Con destinatario `Customer`, el contacto lleva el `customer_id` de la cotización, como hoy en la bandeja. Con `Billing` va **sin** `customer_id` (sólo el teléfono): pasar el del cliente haría que el buscador encuentre la conversación del cliente, que es otro número. | propuesta |
| D16c | El PDF se guarda como **media propia de Mensajería** (copia en su almacenamiento, como un adjunto saliente), no como la URL pública de Quotations, que el ciclo de vida del bucket borra. El hilo muestra el mensaje con el documento descargable. | propuesta |
| D18 | **No hay sender de log ni cuenta global en ningún entorno.** En local se prueba con una conexión real (el owner configura el tenant local Origen Botánico). Las pruebas automáticas usan dobles: ninguna llama a Zenvia ni a Meta. | owner |
| D17 | **Reemplaza** la regla del spec 2026-10-14 §5 «las dos → envía por Meta»: ahora con más de una disponible decide la elección del tenant (D6). Las demás reglas de esa sección siguen: Meta exige la plantilla `APPROVED` en esa conexión; Zenvia no depende de ninguna plantilla de Meta; el resolver es un Strategy por tenant, sin abstracción de canal genérica para la bandeja ni las campañas. | owner |

## 4. Comportamiento

### 4.1 Qué es un canal disponible

- **Meta:** conexión `whatsapp-cloud` en estado `Active`, visible para el tenant (hoy exige el módulo `messaging`), y
  con la plantilla de cotización `APPROVED` según `IQuotationTemplateReadiness.FindApprovedAsync(tenant, conexión)`.
  Una conexión de Meta con la plantilla en revisión, rechazada o sin aprovisionar **no** es disponible.
- **Zenvia:** Zenvia habilitado para el tenant por el operador, conexión `zenvia` en estado `Active`, visible para el
  tenant (módulo `quotations`), y con `templateId` cargado. Una conexión vieja sin `templateId` no es disponible hasta
  que se edite (§6, despliegue).

### 4.2 Resolución del canal en cada envío

En `POST /api/v1/tenants/{tenantId}/quotations/{id}/send`, antes de cualquier efecto externo (antes de firmar la URL
del PDF, igual que hoy la verificación de estado):

1. Listar los canales disponibles del tenant.
2. Ninguno → 422 `quotation.whatsapp.no_channel`. La cotización sigue en borrador.
3. Si hay una elegida guardada y sigue disponible → esa.
4. Si no (no hay elegida, o dejó de estar disponible):
   - una sola disponible → esa. No se guarda nada: la regla D5 la vuelve a elegir sola en cada envío.
   - varias → el request tiene que traer `connectionId`:
     - sin `connectionId` → 422 `quotation.whatsapp.channel_required`, con la lista de canales en el cuerpo o
       suficiente información para que la pantalla vuelva a pedirla (ver §5.2, la pantalla normalmente ya la tiene).
     - con un `connectionId` que no está entre las disponibles → 422 `quotation.whatsapp.channel_unavailable`.
     - con uno disponible → se usa y **se guarda** como la elegida del tenant (D6, D8), en la misma transacción que
       el paso a `Sent`. Si el envío falla, no se guarda.
5. Con la elegida guardada y vigente, un `connectionId` en el request se rechaza con 422
   `quotation.whatsapp.channel_already_set`: el diálogo de envío no es una forma de cambiar la configuración sin
   `tenancy.settings.update`.

Los nombres de los códigos son propuesta: el constructor puede ajustarlos, siempre con prefijo `quotation.whatsapp.`
como los de hoy (`quotation.whatsapp.recipient_missing`).

### 4.3 Envío por cada proveedor

- **Zenvia:** el sender de hoy, pero con token, número y `templateId` de la conexión resuelta
  (`IIntegrationConnections.ResolveAsync`), no de la configuración global. Mismo payload, mismo log del
  `messageId`, mismas reglas de no registrar teléfono ni URL del PDF.
- **Meta:** plantilla de cotización con el nombre e idioma que devuelve `FindApprovedAsync` (nunca la constante
  `qep_cotizacion`: tras un reintento la vigente puede ser `qep_cotizacion_v2`), encabezado de documento con la URL
  publicada del PDF y las variables `fullname`, `order_number`, `total`, `valid_until` con el mismo formato que hoy
  arma el sender de Zenvia. Queda en el hilo de ese número en esa conexión (D16, D16a, D16b): Messaging expone a
  Quotations un envío propio de la plantilla de cotización que abre o encuentra la conversación, guarda el PDF como
  media (D16c) y registra el mensaje saliente, sin las guardas de la bandeja.
- Hoy el camino de plantillas de la bandeja no acepta encabezado multimedia (`TemplateCatalog.RequireSendable`
  rechaza `media_header`). El envío de la cotización no usa ese chequeo genérico: la plantilla la define
  `QuotationTemplateDefinition` y su vigencia la da `FindApprovedAsync`. Abrir el encabezado de documento a las
  plantillas libres de la bandeja y las campañas queda fuera (§10).
- El teléfono de destino lo sigue resolviendo `SendQuotation` (cliente o facturación, `QuotationRecipient`), igual
  que hoy.
- Una falla del proveedor deja la cotización en borrador y se registra en `IQuotationSendFailureLog` como hoy. El
  diálogo de fallo del frontend tiene que poder decir **por qué canal** falló.

### 4.4 Configuración del canal del tenant

- Se guarda en Quotations (es configuración del consumidor, no de la conexión): la conexión elegida, por tenant.
  Una sola por tenant.
- Lectura: un endpoint pensado para la pantalla de envío y para Configuración (§5.2), que devuelve los canales
  disponibles, la elegida guardada (si sigue disponible) y si hace falta elegir.
- Escritura desde Configuración: `tenancy.settings.update`. Permite elegir entre las disponibles; con una sola
  disponible no hay nada que elegir y la sección lo dice.
- Escritura desde el envío: sólo como se describe en §4.2, paso 4.

### 4.5 Habilitación de proveedores por tenant (operador)

- El catálogo de proveedores declara, por proveedor, si requiere habilitación por tenant. Hoy sólo Zenvia.
- La habilitación es estado guardado por tenant y proveedor, con su historial (quién, cuándo, motivo, nota), como los
  módulos.
- Efecto de un proveedor deshabilitado en el tenant:
  - no aparece en el catálogo de Integraciones del tenant (`GET .../integrations/catalog`);
  - sus conexiones existentes siguen en el listado con una marca de «deshabilitado por la plataforma» (D15);
  - crear, editar, probar o reanudar una conexión suya responde 403 con código propio (propuesta:
    `integrations.provider.not_enabled`), distinto de `tenancy.module_not_enabled`, para que la pantalla explique
    lo correcto;
  - `IIntegrationConnections.ResolveAsync` / `ListActiveAsync` no lo devuelven, así que ningún consumidor lo usa.
- La consola de operador muestra la habilitación en la ficha del tenant (`GET .../operator/tenants/{id}`) y la
  cambia con un endpoint propio bajo el mismo grupo `/operator`, con `operator.modules.manage` (o un permiso nuevo
  si el constructor lo justifica). El historial del tenant (`GET .../history`) incluye estos cambios.
- Tenants nuevos: el registro (`register-tenant`) no habilita Zenvia.

## 5. Contratos que necesita la pantalla

La pantalla manda el contrato (regla BFF del frontend): el botón de envío vive en el detalle **y** en el menú de cada
fila del listado, y la fila no trae nada de canales. Por eso la disponibilidad es **por tenant**, en un endpoint
propio, y no un campo del detalle de la cotización.

### 5.1 Integraciones

- El proveedor `zenvia` gana el campo `templateId` (texto, requerido). El formulario de conexión del frontend se
  arma desde el catálogo, así que no debería necesitar código propio más allá de lo que ya hace con los campos.
- El listado y el detalle de conexiones indican si el proveedor está deshabilitado por la plataforma, para pintar el
  estado y bloquear las acciones.

### 5.2 Canales de envío de cotizaciones (Quotations)

Lectura (nombre y forma son propuesta):

```
GET /api/v1/tenants/{tenantId}/quotations/whatsapp-channels
→ 200 {
    "channels": [
      { "connectionId": "…", "provider": "whatsapp-cloud" | "zenvia", "name": "WhatsApp sede norte",
        "phone": "+57 300 123 4567" }
    ],
    "selectedConnectionId": "…" | null,   // la elegida guardada, sólo si sigue disponible
    "needsChoice": true | false            // más de una disponible y ninguna elegida vigente
  }
```

Permiso: el de enviar (`quotations.quotation.manage`). Lo usan el botón de envío (detalle y listado) y la sección de
Configuración. El envío no se apoya en esta respuesta para decidir: el backend vuelve a resolver en el `send`.

Escritura desde Configuración: `PUT /api/v1/tenants/{tenantId}/quotations/whatsapp-channel` con
`{ "connectionId": "…" }`, `tenancy.settings.update`; 422 si la conexión no está disponible.

Envío: el body de `POST .../send` gana `connectionId` opcional, con las reglas de §4.2.

### 5.3 Consola de operador

- La ficha del tenant incluye los proveedores que requieren habilitación, con su estado y su último cambio.
- Un endpoint de cambio con motivo y nota, que valida el motivo por dirección como los módulos.

## 6. Frontend

- **Consola de operador** (`features/operator`): sección «Proveedores» en la ficha del tenant, al lado de los
  módulos, con un interruptor por proveedor y el mismo diálogo de confirmación con motivo y nota que los módulos.
  El interruptor muestra el estado del servidor y nunca cambia solo, como `ModuleCard`. El historial muestra los
  cambios de proveedor.
- **Integraciones** (`features/integrations`): Zenvia no aparece en el catálogo si está deshabilitado. Sus conexiones
  existentes se ven con el estado «Deshabilitado por la plataforma», sin acciones salvo borrar.
- **Cotizaciones** (`features/quotes`):
  - el botón «Enviar por WhatsApp» del detalle y la acción del menú de fila leen `whatsapp-channels`;
  - sin canales: deshabilitado, con una explicación que lleve a Integraciones (a quien tenga permiso de
    verlas) o que diga que lo pida a un administrador;
  - con `needsChoice`: al tocar enviar, antes de la pregunta del destinatario que ya existe, un diálogo para elegir
    el canal (número, nombre y proveedor de cada conexión), que avisa que la elección queda para todos los envíos;
  - mapear los códigos nuevos de §4.2 a mensajes propios en el diálogo de fallo, y un `channel_required` que llegue
    igual (otra pestaña, la elegida dejó de estar disponible) vuelve a abrir el diálogo de elección.
- **Configuración** (`features/tenant-settings`): sección «Envío de cotizaciones por WhatsApp» para ver y cambiar la
  conexión elegida, con `tenancy.settings.update`.
- **Mensajería** (`features/messaging`): el hilo pinta el mensaje de plantilla de cotización con su documento
  descargable (nombre del archivo, abrir o bajar), como un adjunto de documento saliente. Hoy un mensaje de
  plantilla no trae media.
- Cada pantalla tocada cubre carga, vacío, error y éxito (CLAUDE.md del frontend).

## 7. Despliegue

1. **Antes** de desplegar el backend en producción, Origen Botánico tiene que quedar con su propia conexión de
   Zenvia (token, número y `templateId` de la cuenta que hoy está en `Quotations__WhatsApp__*`) y Zenvia habilitado.
   Como la migración habilita Zenvia a quien ya tiene una conexión (D14), alcanza con que la conexión exista antes de
   la migración; si no, el operador lo habilita después. Sin esto, Origen Botánico pierde el envío el día del
   despliegue.
2. Las conexiones de Zenvia existentes no tienen `templateId`: no son disponibles hasta que se editen. El constructor
   decide si la migración copia el `TemplateId` global a la conexión de Origen Botánico (si se puede identificar sin
   ambigüedad) o si se carga a mano en el paso 1.
3. Se retiran las variables `Quotations__WhatsApp__*` del entorno después del despliegue, y la validación de
   `QuotationsOptionsValidator` que las exige en `Production`.
4. Backend primero, frontend después. Un frontend viejo contra el backend nuevo sigue funcionando salvo en el caso
   `needsChoice`, donde el envío responde `channel_required` y el viejo lo muestra como un error genérico: aceptable
   por la ventana corta entre los dos despliegues.

## 8. Lo que queda al constructor

- Dónde vive el estado de habilitación (Integrations, dueño del catálogo, parece lo natural) y cómo la ficha del
  tenant de la consola (Tenancy) lo lee: puerto en Tenancy y adaptador en Bootstrapper, como el resto de los cruces.
- La forma exacta de los DTOs y de los códigos de error, respetando el prefijo de cada módulo.
- Cómo se parte el sender: un Strategy por proveedor detrás de un resolver por tenant (§3, D17).
- Cómo Messaging expone el envío de la plantilla de cotización a Quotations (puerto en Quotations, adaptador en
  Bootstrapper) y cómo resuelve la conversación del cliente.
- Las pruebas de integración que hoy envían cotizaciones dependen de `LogWhatsAppSender` cuando no hay
  configuración global. Con D1 y D18 tienen que preparar un canal con un doble registrado sólo en el host de pruebas
  (conexión de prueba más un doble del Strategy); ninguna prueba llama a Zenvia ni a Meta de verdad, y ningún doble
  se registra en la aplicación.
- Cómo se copia el PDF a Mensajería (bytes desde el almacenamiento de Quotations por un puerto, o subida desde la URL
  publicada) y qué se le manda a Meta en el encabezado (`link` o `id` de media).
- Si el permiso del operador para proveedores es `operator.modules.manage` o uno nuevo.

## 9. Criterios de éxito

1. Un tenant con una sola conexión disponible (Meta o Zenvia) envía por ella sin configurar nada.
2. Un tenant con varias disponibles y ninguna elegida ve el diálogo al enviar; la elección se guarda y el siguiente
   envío no pregunta, también para otro usuario.
3. Un asesor sin `tenancy.settings.update` puede hacer la primera elección desde el envío, pero no cambiarla
   después; un administrador la cambia en Configuración.
4. Un tenant sin canales ve el botón deshabilitado con su explicación, y un `send` directo responde
   `quotation.whatsapp.no_channel` sin tocar la cotización.
5. El operador habilita y deshabilita Zenvia en un tenant, con motivo y nota, y lo ve en el historial. Un tenant
   nuevo nace sin Zenvia. Deshabilitado, Zenvia no aparece en el catálogo del tenant, sus conexiones quedan
   bloqueadas y no envían.
6. Ningún envío usa la cuenta global; `rg "Quotations:WhatsApp|Quotations__WhatsApp"` no encuentra configuración
   viva.
7. Una cotización enviada por Meta aparece en el hilo de ese número en Mensajería, con el PDF descargable también
   después de que venza la copia pública; el envío no cambia el estado ni la asignación de la conversación, y lo
   puede hacer un asesor sin permisos de Mensajería.

## 10. Fuera de alcance

- Enviar la cotización por otro canal que no sea WhatsApp (correo, Instagram).
- Elegir el canal por envío una vez guardada la elección (para eso está Configuración).
- Que el operador vea o edite las conexiones de los tenants (sigue pendiente desde el spec 2026-10-08).
- Habilitación por tenant de otros proveedores: la estructura queda lista (D12), pero sólo Zenvia la usa.
- Plantillas libres con encabezado de documento en la bandeja y en las campañas.
- Formatear monto y fecha de la plantilla con el formato regional del tenant (hoy `es-CO` fijo en el sender).
