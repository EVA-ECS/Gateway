# Gateway

Das Gateway ist der öffentliche Einstiegspunkt für den Chat.

Im einfachen MVP:

- nimmt es Nachrichten vom authentifizierten Client an und veröffentlicht
  `ChatMessageEvent` über RabbitMQ/MassTransit; Storage konsumiert daraus
  `storage_queue`,
- setzt beim WebSocket-Login `presence:<userId>` mit einer TTL in Redis,
- hört auf `gateway:delivery` und schickt zugestellte Nachrichten an den
  passenden lokalen WebSocket zurück.

Am WebSocket akzeptiert Gateway sowohl die einfache Form mit `targetId` und
`text` als auch die aktuell vom Frontend verwendete Form mit
`message.payload.ciphertext`. Intern wird in beiden Fällen derselbe einfache
`ChatMessageEvent` verwendet. Beim Rückweg wird dieser Event wieder in die vom
Frontend erwartete Payload-Form übersetzt.

Der Gateway-Prozess speichert keine Chatnachrichten. Die dauerhafte Speicherung
erfolgt im Storage Service. Der Redis-Sende-Lock schützt nur einzelne
WebSocket-Verbindungen; RabbitMQ-Acknowledgements werden von MassTransit
verwaltet.
