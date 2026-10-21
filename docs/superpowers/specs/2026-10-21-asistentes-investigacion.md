# Asistentes (bots de WhatsApp): investigación y concepto

- **Estado:** notas de investigación y concepto, 2026-10-21. No es un spec de diseño: los specs salen de acá
  (§7), en el orden que decidió el developer.
- **Mockup:** https://claude.ai/artifact/G4YgH6Mq9wozG9iBBziCWh (privado; cinco pantallas más la biblioteca de
  plantillas).
- **Alcance decidido:** el módulo se llama **Asistentes** y por ahora tiene **sólo bots de flujo**. El
  **asistente con IA no se muestra ni se implementa** todavía; su investigación queda en §4 para cuando se retome.
- **Caso de referencia:** el tenant Clasic, cuyo bot hoy vive en su propio backend y sale por lulo-chat. **No se
  copia su implementación** (ni la de lulo-chat): se usa para saber qué necesita el negocio, no cómo construirlo.

## 1. Qué se investigó y dónde

| Plataforma | Qué se miró | Fuente |
| --- | --- | --- |
| Wati | Nodos del constructor: enviar mensaje (texto, imagen, video, audio, documento), preguntar (botones hasta 3, listas hasta 10, guardar en variable, validación número/fecha/hora/regex, ruta «respuesta no esperada»), condición (variable, operador, Y/O, rama verde/roja), asignar a usuario o equipo, disparar otro chatbot, nodos de IA al final del flujo; iconos de canal que muestran qué nodos soporta cada canal; biblioteca de plantillas | [Nodos](http://support.wati.io/en/articles/11463029-guide-to-chatbot-nodes-in-wati), [Primer chatbot](https://support.wati.io/en/articles/13934658-how-to-create-and-build-your-first-chatbot-in-wati), [Asignar](https://support.wati.io/en/articles/11463034-understanding-the-assign-user-and-assign-team-chatbot-nodes) |
| Zendesk | Bloque de horario con ramas «abierto» y «cerrado»; el traspaso («Escalation») es el **último bloque** de una rama; «Validate dialogue» antes de publicar; el agente corre 24/7 salvo que el flujo mire el horario | [Horario y traspaso](https://support.zendesk.com/hc/en-us/articles/7494938880026-Workflow-Notify-customers-of-business-hours-when-no-agents-are-online) |
| Chatwoot | Cola del bot por **estado** (`pending` = bot, `open` = humano); el asesor devuelve al bot poniendo `pending`; Captain con mensajes de traspaso y resolución, auto-resolución por inactividad, playground, y **audiencia y horario** por asistente (siempre / en horario / fuera de horario), evaluados al empezar y al reabrir | [Agent bots](https://www.chatwoot.com/hc/user-guide/articles/1677497472-how-to-use-agent-bots), [Captain](https://www.chatwoot.com/blog/captain-custom-instructions), [Audiencia y horario](https://www.chatwoot.com/hc/user-guide/articles/1786119055-control-who-captain-replies-to-and-when-audience-schedule) |
| Intercom | Fin (contenido, guía, traspaso) y Workflows (disparadores: cualquier mensaje, visita a página, cambio de estado); **asignar no interrumpe la automatización, responder sí** | [AI y automatización](https://www.intercom.com/help/en/articles/9638929-get-the-best-from-ai-and-automation), [Asignación](https://www.intercom.com/help/en/articles/6561699-assign-conversations-to-teammates-and-teams) |
| respond.io | Paso de IA con objetivo (responder / recolectar datos), persona, fuentes, «responder fuera de las fuentes», ramas de salida (éxito, pide humano, no sabe, inactivo 60 min, otros errores); habilidades del agente (asignar, cerrar, HTTP, disparar workflow) | [AI Objective](https://respond.io/help/workflows/step-ai-objective), [Disparar workflow](https://respond.io/help/ai-agent-actions/ai-agent-action-trigger-workflow) |
| Tidio | Flows (guiado) y Lyro (IA) como productos separados que conviven con los agentes; biblioteca de más de 40 plantillas | [Flows, Lyro y agentes](https://help.tidio.com/hc/en-us/articles/14667264947356-How-do-flows-Lyro-and-live-agents-work-together) |
| Kommo | Pausas «hasta que responda», por temporizador o fuera de horario; sólo un bot a la vez por conversación; el bot para cuando la conversación se cierra | [Interrupciones](https://support.kommo.com/docs/manage-salesbot-interruptions.md) |
| HubSpot | Final «Send to team member»; el chatflow sólo se muestra cuando hay gente disponible | [Comunidad](https://community.hubspot.com/t/control-your-chatbot-availability-with-new-custom-settings/33243) |

## 2. Patrones que se repiten (y que adopta QEP)

1. **Dos tipos de automatización en el mismo módulo**, flujo guiado y agente de IA, y la IA como bloque dentro del
   flujo. QEP adopta el módulo único; la IA queda diferida (§4).
2. **Bloques estándar:** mensaje, pregunta (botones, lista, abierta con validación, adjunto), condición, horario,
   espera, asignar, HTTP, ir a otro flujo, traspaso como fin de rama.
3. **Variables de conversación** con tipo, usables en cualquier texto y en condiciones; listas con opciones
   dinámicas desde una colección.
4. **Guardar ≠ publicar.** Borrador, validación con lista de problemas, versión publicada; la conversación termina
   con la versión con que empezó.
5. **El humano manda.** Responder o tomar la conversación pausa el bot; se puede pausar sin tomar y devolver al
   bot. El bot nunca responde en una conversación con asesor.
6. **Horario de atención** como dato del tenant (por conexión), consultado por el bot con un bloque; no se configura
   dentro del bot.
7. **Un bot activo por conexión.** Dos bots en la misma conversación se pisan (Kommo).
8. **Biblioteca de plantillas** como punto de partida (Wati, Tidio, Kommo). §5.
9. **Simulador** para probar sin gastar mensajes.

## 3. Concepto del módulo Asistentes (bots de flujo)

- **Lista:** nombre, conexiones, estado (activo, pausado, borrador), versión publicada y borrador con cambios,
  cifras del día (atendidas, resueltas sin asesor, traspasadas, pausadas por un asesor).
- **Nuevo asistente:** nombre, empezar desde (en blanco, plantilla, duplicar uno existente), conexiones que
  atiende (una conexión tiene un solo asistente activo), cuándo entra (toda conversación nueva o reabierta sin
  asesor; palabra clave; sólo fuera de horario).
- **Editor (React Flow):** paleta por categorías (mensajes, preguntas, lógica, acciones de QEP, integraciones),
  lienzo con bloques tipados y **salidas por handle** (cada botón, «sí/no», «ok/error», «otra respuesta» es una
  salida), panel de propiedades del bloque, aviso de salidas sin conectar, Validar, Simular y Publicar vN. El
  backend es dueño del modelo del flujo; el layout del lienzo se guarda aparte.
- **Bandeja:** chip «La atiende X · paso n de m», botón «Pausar asistente», aviso de que responder pausa,
  filtro «Con asistente», mensajes del bot con autoría propia, historial del asistente y datos capturados en el
  detalle, «Devolver al asistente».
- **Horarios:** entidad con nombre por tenant, asignada por conexión, predeterminado, duplicar, excepciones y
  festivos, «abierto ahora», no se borra uno en uso (spec propio).
- **Bloque HTTP:** referencia una conexión «API externa» del tenant (URL base y clave cifrada en Integrations);
  ruta relativa, cuerpo con variables, mapeo de la respuesta a variables, tiempo de espera corto, rama de error,
  registro de cada llamada sin cuerpo ni clave. Es lo que convierte a Clasic de código en configuración.
- **Envío proactivo:** API por tenant con clave propia para que un sistema externo mande una plantilla a un
  cliente por su conexión (los avisos de visita de Clasic). Pieza aparte del constructor.

## 4. Asistente con IA (diferido: no se muestra ni se implementa todavía)

Lo que la investigación dejó listo para cuando se retome, para no volver a investigar:

**Configuración del asistente** (respond.io, Chatwoot, Intercom):
- *Instrucciones:* cómo se presenta, tono, qué debe hacer, qué hacer fuera de su alcance (decir que no puede y
  ofrecer un asesor, o responder con conocimiento general: no recomendado), máximo de respuestas seguidas sin
  humano.
- *Conocimiento:* documentos (PDF, DOCX), páginas web que se revisan periódicamente, las respuestas rápidas del
  tenant (siempre al día), el catálogo (nombres, precios, disponibilidad en vivo). Interruptor «responder sólo con
  las fuentes». Cada respuesta cita su fuente.
- *Habilidades:* buscar cliente, etiquetar, completar ficha, asignar, resolver, abrir negocio, llamada HTTP,
  entregar a un bot de flujo.
- *Traspaso:* la persona pide humano; no encuentra respuesta; detecta queja; inactividad N minutos → resolver.
  Mensaje al entregar; a quién (sin asignar en horario, al asesor que ya atiende al cliente, repartir).
- *Audiencia y horario:* a quién responde (todos, o por condiciones del cliente) y cuándo (siempre, en horario,
  fuera de horario); se evalúa al empezar y al reabrir, nunca saca al asistente de una conversación en curso.
- *Probar:* panel de chat con el borrador, sin gastar mensajes, que muestra fuentes y traspasos.

**La IA como bloque del flujo:** un bloque «Asistente con IA» con salidas «terminó», «pide humano», «no sabe»,
«inactivo», para que un bot de flujo entregue a la IA en un paso y recupere el control después. Wati lo pone al
final del flujo; respond.io en cualquier punto.

**Lo que QEP necesitaría** y hoy no tiene: proveedor de modelo y su clave por plataforma o por tenant, indexación
de documentos (embeddings y búsqueda), presupuesto y medición de costo por tenant, registro de cada respuesta con
sus fuentes (auditoría), y las reglas de la bandeja de §3, que son las mismas que para el bot de flujo. Por eso el
estado de bot por conversación se diseña **neutral al tipo de asistente**.

## 5. Sistema de plantillas de bots

Qué es: flujos listos para instalar, con textos y bloques armados, que el tenant adapta en vez de empezar en
blanco. Es lo que hace que un negocio nuevo tenga un bot útil en minutos.

**Dos orígenes de plantillas** (decisión del developer, 2026-10-21):
- *Plantillas públicas del sistema* (mantenidas en el producto, versionadas, las ve y reutiliza todo tenant):
  «Menú de bienvenida con traspaso», «Fuera de horario», «¿Dónde va mi pedido?» (Shopify o pedidos de QEP),
  «Calificar antes de ventas», «Respuesta a campaña», «Confirmar pedido». Cada una con categoría, descripción,
  para qué sirve, qué bloques usa, qué necesita (una conexión API externa, el módulo de pedidos) y una vista previa
  del flujo. Viven como archivos en el repo del backend y se cargan al arrancar, como los catálogos de proveedores.
- *Plantillas del tenant*: «Guardar como plantilla» desde el editor; sirven para repetir el bot en otra conexión.

**Lo propio de un solo cliente no es una plantilla: es un bot de su tenant.** El servicio técnico de Clasic, con sus
endpoints y su URL de registro, se arma **desde la cuenta del tenant, con su propio editor**. En esta fase, un
empleado de QEP **asesora y acompaña** al tenant para crearlo (decisión del developer, 2026-10-21). No hay nada
que construir para eso.

**Diferido: bots creados por el operador de la plataforma para un tenant.** Se evaluó y se dejó para una fase
posterior (decisión del developer, 2026-10-21). Queda escrito para no volver a analizarlo. QEP ya tiene la
consola de operador (tenant QCode, sección «Plataforma» del frontend, `/operator/*`, permisos
`operator.tenants.read`, `operator.modules.manage`, `operator.tenants.manage`, historial en
`tenancy.tenant_changes` con motivo). Cuando se retome, se extendería así:
- En el detalle de un tenant de la consola, una sección **«Asistentes»**: lista de los bots del tenant, crear,
  abrir en el mismo editor de React Flow, publicar, pausar, duplicar a otro tenant. El bot queda en el tenant como
  si lo hubiera hecho él: lo ve en su lista, lo edita, lo pausa.
- Permiso nuevo **`operator.assistants.manage`**, sólo en el tenant operador, con la misma regla de hoy: el
  filtro de operador lo conserva únicamente en QCode.
- Cada creación, publicación o pausa hecha por el operador queda en el historial del tenant con motivo, como los
  cambios de módulos; y en el hilo de la conversación el bot aparece con su nombre, sin distinguir quién lo armó.
- Si el bot necesita una conexión «API externa» que el tenant no tiene, el operador la crea desde la misma consola
  (las credenciales las pone quien las tiene: el operador si el cliente se las entregó, o el tenant desde
  Integraciones).
- Las «plantillas asignadas a un tenant» quedan descartadas como concepto: no hacen falta.
- Se prefirió esta forma (endpoints de operador por tenant destino, sobre los mismos handlers) frente a darle al
  operador una membresía en el tenant del cliente (el README lo prohíbe: lo volvería asesor asignable y sin
  motivo en el historial) y frente a un mecanismo de suplantación de tenant (no existe y abriría todos los
  tenants si sale mal).

**Cómo funciona instalar una:** crea un **borrador** del flujo como copia; la plantilla no queda enlazada (una
actualización de la plantilla no toca los bots ya creados). Antes de copiar pide lo mínimo que la plantilla
declara como **parámetros**: nombre del negocio, conexión, horario, y los textos clave que no se pueden adivinar
(por ejemplo la URL de registro). Lo que una plantilla necesita y el tenant no tiene (una conexión API externa, un
módulo apagado) se avisa antes de instalar, no al publicar.

**Dónde se ve:** pestaña «Plantillas» dentro de Asistentes, con filtros por categoría y canal, tarjeta con vista
previa y botón «Usar plantilla»; también como opción «Empezar desde» al crear un asistente.

**Formato:** la plantilla es el mismo modelo del flujo que guarda el backend (bloques tipados y transiciones)
más metadatos (nombre, categoría, canales, requisitos, parámetros, versión) y un layout sugerido del lienzo. Las
del sistema viven como archivos en el repo del backend y se cargan al arrancar, como los catálogos de proveedores;
las del tenant, en la base.

## 6. Decisiones tomadas en la conversación del 2026-10-21

- El módulo se construye **en QEP**, dinámico por tenant; se descartó un «bot externo» por HTTP.
- Lienzo en **React Flow** (`@xyflow/react`); el backend es dueño del modelo del flujo.
- Clasic **mantiene su backend** y expone su API con clave; QEP la guarda en una conexión «API externa».
- **IA fuera por ahora.** El estado de bot por conversación se diseña neutral al tipo.
- **Horarios** con nombre por tenant, asignados por conexión, con duplicar; sección propia en Configuración.
- **Plantillas públicas del sistema** para todos los tenants. Lo particular de un cliente se arma desde la cuenta del
  tenant, con un empleado de QEP acompañando. **Diferido:** que el operador cree bots para un tenant desde la
  consola «Plataforma» (`/operator/tenants/{targetTenantId}/assistants/*`), analizado en §5.
- **Envío proactivo** entra al alcance como pieza aparte.
- Orden de construcción: estado de bot + horarios → interactivos salientes → claves de API + envío proactivo →
  constructor (motor con simulador, editor, bloque HTTP) → migración de lulo-chat al final.
- Todo spec cubre backend y frontend. **No se escribe código hasta que el developer lo diga.**

## 7. Specs que salen de acá

1. Bot en la conversación y horarios de atención.
2. Interactivos salientes (botones, listas, `cta_url`).
3. Claves de API por tenant y envío proactivo.
4. Constructor de flujos: motor y simulador; editor React Flow con plantillas; bloque HTTP y conexión «API externa».
5. Migración de lulo-chat (`2026-10-21-migracion-lulo-chat-design.md`, ya en borrador).
