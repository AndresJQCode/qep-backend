-- Corrige la dirección de los mensajes del historial de coexistencia importados con el parser viejo.
--
-- El parser marcaba saliente sólo lo que traía `to`, y Meta nunca lo manda en el historial, así que lo que
-- el negocio escribió quedó como entrante. El historial no se reenvía, y repetir una entrega no sirve porque
-- el ON CONFLICT por wamid la ignora. La fuente es el payload guardado en messaging.webhook_deliveries, que
-- se purga a los 7 días: corre esto antes de que venza.
--
-- Qué hace, y sólo sobre mensajes con imported = true cuyo wamid está en una entrega con `from` presente y
-- distinto del id del hilo:
--   * direction = 2 (Outbound) y status = 1 (Sent), los códigos de MessageColumnCodes.
--   * last_message_direction y last_message_status de la conversación, leídos del mensaje al que apunta
--     last_message_id. Sube version sólo en las que cambian.
--
-- Qué no hace:
--   * No toca status ni unread_count de la conversación (decisión del owner). Una conversación que entró
--     abierta por este error sigue abierta. La vista previa dice cuántas son.
--   * No toca last_inbound_at, ni mensajes con imported = false, ni los que no traen `from`.
--   * Las columnas de autor no cambian. El INSERT de CoexistenceIngestion no las escribe para ninguna
--     dirección, así que un saliente importado queda con sent_by_kind = 1 por defecto y sin miembro.
--
-- Cómo correrlo. En DBeaver, con Ejecutar script SQL (Alt+X), todo de una vez y en la misma conexión. Revisa
-- los números de la vista previa y los del final. Por defecto termina en ROLLBACK y no deja nada escrito.
-- Si los números cuadran, cambia la última línea por COMMIT y córrelo otra vez. Una segunda corrida con
-- COMMIT cambia 0 filas.
BEGIN;

-- Cada mensaje del historial en las entregas guardadas. Un trozo mal formado se salta, como en el parser. El
-- from cuenta sólo si es un texto no vacío, y se compara en dígitos, igual que WebhookPayloadParser.
CREATE TEMP TABLE repair_history ON COMMIT DROP AS
SELECT DISTINCT
       message ->> 'id' AS wamid,
       regexp_replace(thread ->> 'id', '[^0-9]', '', 'g') AS wa_id,
       CASE
           WHEN jsonb_typeof(message -> 'from') IS DISTINCT FROM 'string' OR (message ->> 'from') !~ '[^[:space:]]' THEN 'no_from'
           WHEN regexp_replace(message ->> 'from', '[^0-9]', '', 'g') = regexp_replace(thread ->> 'id', '[^0-9]', '', 'g') THEN 'contact'
           ELSE 'business'
       END AS origin
  FROM messaging.webhook_deliveries d
 CROSS JOIN LATERAL jsonb_array_elements(CASE jsonb_typeof(d.payload -> 'entry') WHEN 'array' THEN d.payload -> 'entry' ELSE '[]'::jsonb END) AS entry
 CROSS JOIN LATERAL jsonb_array_elements(CASE jsonb_typeof(entry -> 'changes') WHEN 'array' THEN entry -> 'changes' ELSE '[]'::jsonb END) AS change
 CROSS JOIN LATERAL jsonb_array_elements(CASE jsonb_typeof(change -> 'value' -> 'history') WHEN 'array' THEN change -> 'value' -> 'history' ELSE '[]'::jsonb END) AS chunk
 CROSS JOIN LATERAL jsonb_array_elements(CASE jsonb_typeof(chunk -> 'threads') WHEN 'array' THEN chunk -> 'threads' ELSE '[]'::jsonb END) AS thread
 CROSS JOIN LATERAL jsonb_array_elements(CASE jsonb_typeof(thread -> 'messages') WHEN 'array' THEN thread -> 'messages' ELSE '[]'::jsonb END) AS message
 WHERE change ->> 'field' = 'history'
   AND message ->> 'id' IS NOT NULL;

-- Los mensajes por corregir. El filtro por direction es lo que hace idempotente el script.
CREATE TEMP TABLE repair_messages ON COMMIT DROP AS
SELECT m.id, m.conversation_id
  FROM messaging.messages m
  JOIN messaging.conversations c ON c.id = m.conversation_id
  JOIN repair_history h ON h.wamid = m.wamid AND h.wa_id = c.wa_id
 WHERE h.origin = 'business'
   AND m.imported
   AND m.direction = 1;

-- Vista previa. Sólo conteos, sin texto de mensajes.
SELECT 'payload: mensajes del negocio' AS paso, count(*) AS filas FROM repair_history WHERE origin = 'business'
UNION ALL
SELECT 'payload: mensajes del contacto', count(*) FROM repair_history WHERE origin = 'contact'
UNION ALL
SELECT 'payload: mensajes sin from (quedan entrantes)', count(*) FROM repair_history WHERE origin = 'no_from'
UNION ALL
SELECT 'mensajes a corregir', count(*) FROM repair_messages
UNION ALL
SELECT 'payload: del negocio pero en vivo (no se tocan)', count(*)
  FROM messaging.messages m
  JOIN repair_history h ON h.wamid = m.wamid
 WHERE h.origin = 'business' AND NOT m.imported
UNION ALL
SELECT 'importados entrantes sin entrega (no se pueden corregir)', count(*)
  FROM messaging.messages m
 WHERE m.imported AND m.direction = 1
   AND NOT EXISTS (SELECT 1 FROM repair_history h WHERE h.wamid = m.wamid)
UNION ALL
SELECT 'conversaciones afectadas', count(DISTINCT conversation_id) FROM repair_messages
UNION ALL
SELECT 'conversaciones abiertas que seguirán abiertas con el último del negocio', count(*)
  FROM messaging.conversations c
 WHERE c.status = 'Open'
   AND c.last_message_id IN (SELECT id FROM repair_messages);

-- Los mensajes.
WITH fixed AS (
    UPDATE messaging.messages m SET
        direction = 2,
        status    = 1
      FROM repair_messages r
     WHERE m.id = r.id
       AND m.imported
       AND m.direction = 1
    RETURNING 1)
SELECT 'mensajes corregidos' AS paso, count(*) AS filas FROM fixed;

-- La foto de la lista. last_message_id ya es el más reciente que no es un evento: lo dejó ahí la ingesta con
-- su regla de empates (el timestamp de Meta es de segundos), y ordenar aquí por occurred_at no los resuelve.
-- La vista previa del texto no cambia, porque el mensaje es el mismo. Sólo se escribe si algo cambia, para
-- no darle un 412 al asesor que tenga la conversación abierta.
WITH fixed AS (
    UPDATE messaging.conversations c SET
        last_message_direction = m.direction,
        last_message_status    = m.status,
        updated_at             = now(),
        version                = c.version + 1
      FROM messaging.messages m
     WHERE m.id = c.last_message_id
       AND c.id IN (SELECT conversation_id FROM repair_messages)
       AND (c.last_message_direction IS DISTINCT FROM m.direction
            OR c.last_message_status IS DISTINCT FROM m.status)
    RETURNING 1)
SELECT 'conversaciones con foto corregida' AS paso, count(*) AS filas FROM fixed;

-- Cambia esta línea por COMMIT después de revisar los números.
ROLLBACK;
