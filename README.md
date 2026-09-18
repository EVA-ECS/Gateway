# Gateway

Das Gateway ist der öffentliche Einstiegspunkt für den Chat.

Im einfachen MVP:

- nimmt es Nachrichten vom authentifizierten Client an und veröffentlicht
  `ChatMessageEvent` über RabbitMQ/MassTransit; Storage konsumiert daraus
  `storage_queue`,
- setzt beim WebSocket-Login `eva-chat:online:<userId>` mit einer TTL in Redis,
- hört auf `gateway:delivery` und schickt zugestellte Nachrichten an den
  passenden lokalen WebSocket zurück.

Am WebSocket akzeptiert Gateway `chat.message.send` mit `targetId`, `requestId`
und `text`. Dieses `text` ist bereits verschlüsseltes JSON, kein Klartext.
`ChatMessageEvent.Ciphertext` enthält das vom
Browser erzeugte JSON-Envelope; das Gateway entschlüsselt es nicht. Sender und
Empfänger im Envelope müssen zur angemeldeten Identität und zum Ziel passen.
Beim Rückweg werden die tatsächlichen Event-Felder weitergegeben, keine
erfundenen Schlüssel, Signaturen oder IVs.

Die WebSocket-Antwort mit Status `published` bestätigt nur die Veröffentlichung
an RabbitMQ, nicht die Speicherung oder den Empfang. `requestId` ordnet die
Antwort dem Sendeversuch zu; `messageId` ist die serverseitige Nachrichten-ID.

## Verlauf und Konfiguration

`GET /api/chat/history/{otherUserId}?limit=50&before=<cursor>` benötigt einen
Bearer-Token. Die Antwort enthält `messages` und `nextCursor`. Gelesen wird
mit dem Benutzer-Token und Supabase-RLS, nicht mit einem Backend-Secret.
Benötigt werden `Supabase__Url` und `Supabase__PublishableKey` sowie Zeins
private Raum-/Empfänger-Zuordnung (`messages.receiver_id`, `rooms.is_group`).
Diese Änderung führt keine Datenbankmigrationen aus.

Gateway und Delivery müssen denselben `Redis__PresenceKeyPrefix`
(`eva-chat:online:`) und `Redis__SingleGatewayDeliveryChannel`
(`gateway:delivery`) verwenden. Es bleibt beim Ein-Gateway-MVP.

Ein neuer WebSocket ersetzt den bisherigen desselben Benutzers mit Close-Code
4001. Der alte Browser darf danach nicht automatisch um die Verbindung kämpfen.

## Lokal prüfen

Contracts als benachbartes Repository auschecken. Kein privater NuGet-Token nötig:

```powershell
dotnet test IntegrationTests/Gateway.IntegrationTests.csproj
# Docker-Build aus dem gemeinsamen Elternordner:
docker build -f Gateway/Dockerfile -t eva-gateway-review .
```

Die ergänzenden 13 Integrationseinheitstests liegen absichtlich unter
`IntegrationTests`, damit Robins offene Test-PRs ihren Ordner `Tests` behalten.
Gemeinsame Änderungen an `Gateway.csproj` müssen beim Zusammenführen erhalten
bleiben. Frontend-Verlauf und Statusanzeige benötigen die passende Frontend-PR.

Der Gateway-Prozess speichert keine Chatnachrichten. Die dauerhafte Speicherung
erfolgt im Storage Service. Der Redis-Sende-Lock schützt nur einzelne
WebSocket-Verbindungen; RabbitMQ-Acknowledgements werden von MassTransit
verwaltet.
