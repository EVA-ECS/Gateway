# EVA Gateway

Für die interne Live-Zustellung registriert jede authentifizierte WebSocket-
Verbindung zwei Redis-Schlüssel mit derselben operativen TTL:

- `presence:<userId>`
- `gateway_for_user:<userId>` mit der konfigurierten Gateway-ID als Wert

Die TTL wird bei `presence.heartbeat` erneuert. Beim Disconnect löscht das
Gateway die Schlüssel nur, wenn das Mapping weiterhin auf diese Gateway-ID
zeigt. Jede Instanz abonniert `gateway:delivery:<gatewayId>` und sendet dort
empfangene `ChatMessagePublishedEvent`-Payloads an den lokalen WebSocket des
Zielbenutzers.

Gateway-ID, TTL, Präfixe und Kanal sind über `Gateway__Id`,
`Gateway__PresenceTtlSeconds`, `Redis__PresenceKeyPrefix`,
`Redis__GatewayMappingKeyPrefix` und `Redis__DeliveryChannelPrefix`
konfigurierbar. Bleibt `Gateway__Id` leer, wird der Container-Hostname
verwendet.
