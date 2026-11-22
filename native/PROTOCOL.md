# Lokales Protokoll Spiel ↔ mett-netd (Version 1)

## Start

Der Launcher startet das Spiel mit diesen Umgebungsvariablen:

| Variable | Inhalt |
|---|---|
| `METT_NET_ADDR` | `127.0.0.1:<port>` des Netzdienstes |
| `METT_NET_TOKEN` | Zugangstoken (bei jedem Launcher-Start neu) |
| `METT_NET_GAME` | Spiel-ID aus dem Katalog |
| `METT_NET_ACTION` | `host`, `join` oder nicht gesetzt |
| `METT_NET_CODE` | bei `join`: Code des Hosts |

Fehlen `ADDR`/`TOKEN`, wurde das Spiel nicht über den Launcher gestartet → Mehrspieler ausblenden.

## Rahmen

TCP-Verbindung zu `METT_NET_ADDR`. Jeder Rahmen:

```
u32 Länge (little endian, zählt Typbyte + Nutzdaten) | u8 Typ | Nutzdaten
```

Maximal 4 MiB. Steuerrahmen enthalten UTF-8-JSON, Datenrahmen Binärdaten:

```
SEND/RECV-Nutzdaten:  u8 Flags (Bit 0 = zuverlässig) | u16 Peer (LE) | Daten
```

Peer `0` ist aus Sicht eines Mitspielers der Host. `65535` heißt beim Senden „an alle“ (nur Host).

## Spiel → Dienst

| Typ | Name | Nutzdaten |
|---|---|---|
| 1 | HELLO | `{"version":1,"token":"…","game":"…"}` – muss der erste Rahmen sein |
| 2 | HOST | `{"max_peers":4}` (0 = unbegrenzt) |
| 3 | JOIN | `{"code":"…"}` |
| 4 | SEND | Datenrahmen |
| 5 | LEAVE | `{}` |
| 6 | KICK | `{"peer":2}` (nur Host) |

## Dienst → Spiel

| Typ | Name | Nutzdaten |
|---|---|---|
| 101 | WELCOME | `{"id":"…","name":"…"}` |
| 102 | HOSTED | `{"code":"…"}` |
| 103 | JOINED | `{"host_id":"…","host_name":"…"}` |
| 104 | PEER_JOINED | `{"peer":1,"id":"…","name":"…"}` |
| 105 | PEER_LEFT | `{"peer":1,"reason":"…"}` |
| 106 | RECV | Datenrahmen (Peer = Absender) |
| 107 | ERROR | `{"message":"…"}` |
| 108 | SESSION_ENDED | `{"reason":"…"}` |

Ein Spiel hat höchstens eine Sitzung gleichzeitig. Pro Rechner kann ein Spiel nur einmal gehostet werden.
Unzuverlässige Nachrichten über der Datagrammgröße (ca. 1 KB) werden mit ERROR abgelehnt.

## Zwischen Launchern (zur Info)

iroh-Verbindung mit ALPN `mett/net/1`. Der Beitretende öffnet einen bidirektionalen Stream und schickt
`{"version":1,"game":"…","name":"…"}`; der Host antwortet `{"ok":true,"name":"…"}` oder `{"ok":false,"reason":"…"}`.
Danach trägt der Stream zuverlässige Nachrichten (`u32 Länge | Daten`), QUIC-Datagramme die unzuverlässigen.
