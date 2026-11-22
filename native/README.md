# Mehrspieler über den Mett Launcher

Spiele im Mett Launcher können sich **ohne Account und ohne eigenen Server** miteinander verbinden.
Der Launcher betreibt dafür den Netzdienst `mett-netd` (auf Basis von [iroh](https://github.com/n0-computer/iroh));
das Spiel spricht nur lokal mit ihm.

```
Spiel A ──TCP 127.0.0.1── mett-netd (Launcher A) ══ iroh/QUIC, verschlüsselt ══ mett-netd (Launcher B) ──TCP── Spiel B
```

- **Identität:** Jeder Launcher hat einen eigenen Schlüssel (`%LocalAppData%\MettLauncher\net\secret.key`).
  Der öffentliche Schlüssel ist gleichzeitig der **Code** zum Beitreten.
- **Verbindung:** iroh findet den Host über seinen Code, schlägt sich direkt durch Router durch (Hole-Punching)
  und weicht nur notfalls auf ein Relay von n0 aus. Relays sehen nur verschlüsselte Pakete.
- **Rollen:** Ein Spieler hostet, die anderen treten bei. Mitspieler senden an den Host, der Host an einzelne
  Mitspieler oder an alle. Wie der Spielzustand verteilt wird, entscheidet das Spiel (Host als Autorität).
- **Nachrichten:** zuverlässig und geordnet (Chat, Ereignisse) oder unzuverlässig als Datagramm (Positionen, ca. 1 KB).

## Spiel anbinden (Rust)

```toml
[dependencies]
mett-net = { git = "https://github.com/Mett-Gaming/mett-launcher", tag = "v0.2.0" }
```

```rust
use mett_net::{MettNet, Event, LaunchAction, Reliability, HOST, BROADCAST};

// Beim Start: nur vorhanden, wenn das Spiel über den Launcher gestartet wurde.
let mut net = MettNet::from_env()?;              // Option<MettNet>
if let Some(net) = net.as_mut() {
    match net.launch_action() {                    // was der Spieler im Launcher gewählt hat
        LaunchAction::Host => net.host(4)?,        // bis zu 4 Mitspieler
        LaunchAction::Join(code) => net.join(&code)?,
        LaunchAction::None => {}                   // Spiel kann selbst ein Menü anbieten
    }
}

// Jeden Frame:
if let Some(net) = net.as_mut() {
    for ev in net.poll() {                         // blockiert nie
        match ev {
            Event::Hosted { code } => { /* Code anzeigen – der Launcher zeigt ihn auch */ }
            Event::PeerJoined { peer, name, .. } => { /* Spieler einfügen */ }
            Event::PeerLeft { peer, .. } => { /* Spieler entfernen */ }
            Event::Message { from, data, .. } => { /* anwenden */ }
            Event::SessionEnded { .. } | Event::Disconnected => { /* zurück ins Menü */ }
            _ => {}
        }
    }
    net.send(BROADCAST, &state_bytes, Reliability::Unreliable)?; // Host
    net.send(HOST, &input_bytes, Reliability::Unreliable)?;      // Mitspieler
}
```

Im Katalog (`games.json`) bekommt das Spiel `"multiplayer": true`. Dann zeigt der Launcher
**„Mit Freunden spielen“** (Spiel hosten / Beitreten mit Code), startet das Spiel passend und blendet den Code
zum Weitergeben ein.

Ein lauffähiges Beispiel ist `mett-net/examples/chat.rs`.

## Andere Engines

Das lokale Protokoll ist bewusst einfach und in [PROTOCOL.md](PROTOCOL.md) beschrieben – jede Sprache mit TCP und JSON
kann es sprechen (C#, C++, GDScript, …).

## Bauen und testen

```powershell
cd native
cargo test --workspace
cargo build --release -p mett-netd   # wird vom Launcher-Build automatisch mitkopiert
```
