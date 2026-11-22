//! Mehrspieler für Spiele, die über den Mett Launcher gestartet werden.
//!
//! Der Launcher betreibt die Peer-to-Peer-Verbindungen (iroh); das Spiel spricht nur lokal mit ihm.
//! Kein Account, kein eigener Server, keine async-Laufzeit im Spiel:
//!
//! ```no_run
//! use mett_net::{MettNet, Event, Reliability, LaunchAction};
//!
//! let Some(mut net) = MettNet::from_env().expect("Netzdienst") else {
//!     return; // nicht über den Launcher gestartet -> nur Einzelspieler
//! };
//! match net.launch_action() {
//!     LaunchAction::Host => net.host(4).unwrap(),
//!     LaunchAction::Join(code) => net.join(&code).unwrap(),
//!     LaunchAction::None => {}
//! }
//! loop {
//!     for ev in net.poll() {
//!         match ev {
//!             Event::Hosted { code } => println!("Code für Freunde: {code}"),
//!             Event::PeerJoined { peer, name, .. } => println!("{name} ist da ({peer})"),
//!             Event::Message { from, data, .. } => { /* Spielzustand anwenden */ let _ = (from, data); }
//!             _ => {}
//!         }
//!     }
//!     net.send(mett_net::HOST, b"pos 1 2 3", Reliability::Unreliable).ok();
//!     # break;
//! }
//! ```
//!
//! Rollen: Ein Spieler hostet, die anderen treten per Code bei. Mitspieler senden immer an den
//! Host (Peer [`HOST`] = 0); der Host sendet an einzelne Peers oder mit [`BROADCAST`] an alle.
//! Wie der Spielzustand verteilt wird (Host als Autorität), entscheidet das Spiel.

use std::{
    io::{self, BufWriter, Write},
    net::{Shutdown, TcpStream},
    sync::mpsc::{self, Receiver, TryRecvError},
    thread,
};

pub use mett_net_proto::{BROADCAST, HOST_PEER as HOST};
use mett_net_proto::{
    ErrorMsg, Frame, Hello, HostRequest, Hosted, JoinRequest, Joined, KickRequest, PeerJoined, PeerLeft, SessionEnded,
    VERSION, Welcome, env, kind,
};

/// Zustellgarantie einer Nachricht.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Reliability {
    /// Kommt sicher und in Reihenfolge an (Chat, Spielereignisse, Zustandswechsel).
    Reliable,
    /// Kann verloren gehen, ist dafür schneller (Positionen, Eingaben, die ständig neu gesendet werden).
    /// Größe begrenzt (ca. 1 KB) – größere Daten zuverlässig senden.
    Unreliable,
}

/// Was der Spieler im Launcher gewählt hat.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LaunchAction {
    None,
    /// „Spiel hosten“ im Launcher.
    Host,
    /// „Beitreten“ im Launcher, mit dem eingegebenen Code.
    Join(String),
}

/// Ereignisse aus [`MettNet::poll`].
#[derive(Debug, Clone)]
pub enum Event {
    /// Eigene Identität ist bereit.
    Ready { id: String, name: String },
    /// Hosten läuft; `code` an Mitspieler weitergeben (der Launcher zeigt ihn ebenfalls an).
    Hosted { code: String },
    /// Beitritt geklappt.
    Joined { host_id: String, host_name: String },
    PeerJoined { peer: u16, id: String, name: String },
    PeerLeft { peer: u16, reason: String },
    Message { from: u16, data: Vec<u8>, reliability: Reliability },
    /// Sitzung vorbei (Host weg, Verbindung verloren, selbst verlassen).
    SessionEnded { reason: String },
    /// Fehler (z. B. falscher Code, Host nicht erreichbar). Die Verbindung zum Launcher bleibt.
    Error { message: String },
    /// Verbindung zum Launcher verloren – Mehrspieler nicht mehr verfügbar.
    Disconnected,
}

/// Verbindung zum Netzdienst des Launchers.
pub struct MettNet {
    writer: BufWriter<TcpStream>,
    events: Receiver<Event>,
    stream: TcpStream,
    action: LaunchAction,
}

impl MettNet {
    /// Verbindet sich mit dem Launcher, wenn das Spiel über ihn gestartet wurde.
    /// `Ok(None)` heißt: kein Launcher (z. B. direkt gestartet) – Mehrspieler einfach ausblenden.
    pub fn from_env() -> io::Result<Option<Self>> {
        let (Ok(addr), Ok(token), Ok(game)) = (std::env::var(env::ADDR), std::env::var(env::TOKEN), std::env::var(env::GAME)) else {
            return Ok(None);
        };
        let action = match std::env::var(env::ACTION).as_deref() {
            Ok("host") => LaunchAction::Host,
            Ok("join") => std::env::var(env::CODE).map(LaunchAction::Join).unwrap_or(LaunchAction::None),
            _ => LaunchAction::None,
        };
        let mut net = Self::connect(&addr, &token, &game)?;
        net.action = action;
        Ok(Some(net))
    }

    /// Verbindet sich explizit (für Tests oder eigene Startlogik).
    pub fn connect(addr: &str, token: &str, game: &str) -> io::Result<Self> {
        let stream = TcpStream::connect(addr)?;
        stream.set_nodelay(true)?;
        let mut writer = BufWriter::new(stream.try_clone()?);
        Frame::json(kind::HELLO, &Hello { version: VERSION, token: token.to_string(), game: game.to_string() }).write_to(&mut writer)?;
        writer.flush()?;

        let (tx, rx) = mpsc::channel();
        let mut reader = stream.try_clone()?;
        thread::Builder::new().name("mett-net".into()).spawn(move || {
            loop {
                let ev = match Frame::read_from(&mut reader) {
                    Ok(f) => decode(f),
                    Err(_) => {
                        let _ = tx.send(Event::Disconnected);
                        break;
                    }
                };
                if let Some(ev) = ev {
                    if tx.send(ev).is_err() {
                        break;
                    }
                }
            }
        })?;
        Ok(Self { writer, events: rx, stream, action: LaunchAction::None })
    }

    /// Was der Spieler im Launcher gewählt hat (hosten / beitreten).
    pub fn launch_action(&self) -> LaunchAction {
        self.action.clone()
    }

    /// Spiel für Mitspieler öffnen. `max_peers` = 0 heißt unbegrenzt.
    pub fn host(&mut self, max_peers: u16) -> io::Result<()> {
        self.write(Frame::json(kind::HOST, &HostRequest { max_peers }))
    }

    /// Einem Spiel per Code beitreten.
    pub fn join(&mut self, code: &str) -> io::Result<()> {
        self.write(Frame::json(kind::JOIN, &JoinRequest { code: code.to_string() }))
    }

    /// Nachricht senden. Mitspieler senden immer an den Host; der Host an `to` oder [`BROADCAST`].
    pub fn send(&mut self, to: u16, data: &[u8], reliability: Reliability) -> io::Result<()> {
        self.write(Frame::send(to, reliability == Reliability::Reliable, data))
    }

    /// Host: Mitspieler entfernen.
    pub fn kick(&mut self, peer: u16) -> io::Result<()> {
        self.write(Frame::json(kind::KICK, &KickRequest { peer }))
    }

    /// Sitzung verlassen (Host: Spiel für alle beenden).
    pub fn leave(&mut self) -> io::Result<()> {
        self.write(Frame { kind: kind::LEAVE, payload: b"{}".to_vec() })
    }

    /// Alle seit dem letzten Aufruf eingegangenen Ereignisse – blockiert nie. Einmal pro Frame aufrufen.
    pub fn poll(&mut self) -> Vec<Event> {
        let _ = self.writer.flush();
        let mut out = Vec::new();
        loop {
            match self.events.try_recv() {
                Ok(e) => out.push(e),
                Err(TryRecvError::Empty) => break,
                Err(TryRecvError::Disconnected) => break,
            }
        }
        out
    }

    fn write(&mut self, f: Frame) -> io::Result<()> {
        f.write_to(&mut self.writer)?;
        // Zuverlässige Steuerbefehle sofort raus; Daten bündelt `poll` pro Frame.
        if f.kind != kind::SEND {
            self.writer.flush()?;
        }
        Ok(())
    }
}

impl Drop for MettNet {
    fn drop(&mut self) {
        let _ = self.writer.flush();
        let _ = self.stream.shutdown(Shutdown::Both);
    }
}

fn decode(f: Frame) -> Option<Event> {
    Some(match f.kind {
        kind::WELCOME => {
            let w: Welcome = f.parse().ok()?;
            Event::Ready { id: w.id, name: w.name }
        }
        kind::HOSTED => Event::Hosted { code: f.parse::<Hosted>().ok()?.code },
        kind::JOINED => {
            let j: Joined = f.parse().ok()?;
            Event::Joined { host_id: j.host_id, host_name: j.host_name }
        }
        kind::PEER_JOINED => {
            let p: PeerJoined = f.parse().ok()?;
            Event::PeerJoined { peer: p.peer, id: p.id, name: p.name }
        }
        kind::PEER_LEFT => {
            let p: PeerLeft = f.parse().ok()?;
            Event::PeerLeft { peer: p.peer, reason: p.reason }
        }
        kind::RECV => {
            let (from, reliable, data) = f.split_data().ok()?;
            Event::Message {
                from,
                data: data.to_vec(),
                reliability: if reliable { Reliability::Reliable } else { Reliability::Unreliable },
            }
        }
        kind::SESSION_ENDED => Event::SessionEnded { reason: f.parse::<SessionEnded>().map(|s| s.reason).unwrap_or_default() },
        kind::ERROR => Event::Error { message: f.parse::<ErrorMsg>().map(|e| e.message).unwrap_or_default() },
        _ => return None,
    })
}
