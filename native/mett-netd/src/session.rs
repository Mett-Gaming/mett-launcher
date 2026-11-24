//! Lokale Verbindung eines Spiels zum Dienst und die Rollen Host/Mitspieler.

use std::{
    collections::HashMap,
    sync::{
        Arc,
        atomic::{AtomicU16, Ordering},
    },
    time::Duration,
};

use anyhow::{Context, Result, bail};
use iroh::{
    EndpointId,
    endpoint::{Connection, RecvStream, SendStream},
};
use mett_net_proto::{
    BROADCAST, ErrorMsg, Frame, HOST_PEER, Hello, HostRequest, Hosted, JoinRequest, Joined, KickRequest, MAX_FRAME,
    PeerHello, PeerJoined, PeerLeft, PeerWelcome, SessionEnded, VERSION, Welcome, kind,
};
use serde_json::json;
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::TcpStream,
    sync::{Mutex, mpsc},
};

use crate::{
    ALPN, Shared,
    peer::{PeerLink, read_json, spawn_pumps, write_json},
};

/// Fehlermeldung, wenn noch kein Anzeigename festgelegt ist.
pub const NAME_REQUIRED: &str = "Bitte lege im Mett Launcher zuerst einen Anzeigenamen fest (Einstellungen → Mehrspieler).";

/// Ein gehostetes Spiel auf diesem Rechner.
pub struct HostState {
    game: String,
    to_game: mpsc::Sender<Frame>,
    peers: Mutex<HashMap<u16, PeerLink>>,
    names: std::sync::Mutex<std::collections::BTreeMap<u16, String>>,
    next_peer: AtomicU16,
    max_peers: u16,
}

impl HostState {
    /// Ändert die Namensliste und gibt sie für das Ereignis zurück.
    fn change_names(&self, change: impl FnOnce(&mut std::collections::BTreeMap<u16, String>)) -> Vec<String> {
        let mut names = self.names.lock().unwrap_or_else(|e| e.into_inner());
        change(&mut names);
        names.values().cloned().collect()
    }

    /// Nimmt einen Mitspieler auf (Handshake ist gelesen).
    pub async fn add_peer(
        self: &Arc<Self>,
        shared: &Arc<Shared>,
        conn: Connection,
        mut send: SendStream,
        recv: RecvStream,
        name: String,
    ) -> Result<()> {
        if self.max_peers > 0 && self.peers.lock().await.len() >= self.max_peers as usize {
            write_json(&mut send, &PeerWelcome { ok: false, name: String::new(), reason: "Das Spiel ist voll.".into() }).await?;
            let _ = send.finish();
            conn.closed().await;
            return Ok(());
        }
        let Some(name) = crate::validate_name(&name) else {
            write_json(&mut send, &PeerWelcome { ok: false, name: String::new(), reason: "Ohne Anzeigenamen kein Beitritt.".into() }).await?;
            let _ = send.finish();
            conn.closed().await;
            return Ok(());
        };
        let host_name = shared.name.read().await.clone();
        write_json(&mut send, &PeerWelcome { ok: true, name: host_name, reason: String::new() }).await?;

        let idx = self.next_peer.fetch_add(1, Ordering::Relaxed).max(1);
        let id = conn.remote_id().to_string();
        let me = Arc::downgrade(self);
        let ev_shared = shared.clone();
        let link = spawn_pumps(conn, send, recv, idx, self.to_game.clone(), move |reason| {
            tokio::spawn(async move {
                if let Some(host) = me.upgrade() {
                    if host.peers.lock().await.remove(&idx).is_some() {
                        let _ = host.to_game.send(Frame::json(kind::PEER_LEFT, &PeerLeft { peer: idx, reason })).await;
                        let count = host.peers.lock().await.len();
                        let names = host.change_names(|n| drop(n.remove(&idx)));
                        ev_shared.event(json!({ "event": "peers", "game": host.game, "role": "host", "count": count, "names": names }));
                    }
                }
            });
        });
        self.peers.lock().await.insert(idx, link);
        let names = self.change_names(|n| drop(n.insert(idx, name.clone())));
        self.to_game.send(Frame::json(kind::PEER_JOINED, &PeerJoined { peer: idx, id, name })).await?;
        let count = self.peers.lock().await.len();
        shared.event(json!({ "event": "peers", "game": self.game, "role": "host", "count": count, "names": names }));
        Ok(())
    }

    async fn send(&self, to: u16, reliable: bool, data: &[u8]) -> Result<()> {
        let peers = self.peers.lock().await;
        if to == BROADCAST {
            for link in peers.values() {
                let _ = link.send(reliable, data).await;
            }
            Ok(())
        } else {
            peers.get(&to).context("Unbekannter Mitspieler")?.send(reliable, data).await
        }
    }

    async fn close_all(&self, reason: &str) {
        for (_, link) in self.peers.lock().await.drain() {
            link.close(reason);
        }
    }
}

enum Role {
    None,
    Host(Arc<HostState>),
    Client(PeerLink),
}

fn sanitize_name(name: &str, id: &str) -> String {
    let n: String = name.chars().filter(|c| !c.is_control()).take(32).collect();
    if n.trim().is_empty() { format!("Spieler-{}", &id[..4.min(id.len())].to_uppercase()) } else { n.trim().to_string() }
}

async fn read_frame(r: &mut tokio::net::tcp::OwnedReadHalf) -> std::io::Result<Frame> {
    let mut len = [0u8; 4];
    r.read_exact(&mut len).await?;
    let len = u32::from_le_bytes(len) as usize;
    if len == 0 || len > MAX_FRAME {
        return Err(std::io::Error::new(std::io::ErrorKind::InvalidData, "ungültige Rahmenlänge"));
    }
    let mut body = vec![0u8; len];
    r.read_exact(&mut body).await?;
    Frame::from_body(body)
}

/// Bedient ein verbundenes Spiel bis zum Trennen.
pub async fn run(shared: Arc<Shared>, stream: TcpStream) -> Result<()> {
    stream.set_nodelay(true)?;
    let (mut rd, mut wr) = stream.into_split();
    let (to_game, mut game_rx) = mpsc::channel::<Frame>(4096);
    tokio::spawn(async move {
        while let Some(f) = game_rx.recv().await {
            if wr.write_all(&f.encode()).await.is_err() {
                break;
            }
        }
    });

    // Erster Rahmen muss HELLO mit gültigem Token sein.
    let first = tokio::time::timeout(Duration::from_secs(10), read_frame(&mut rd)).await.context("HELLO-Timeout")??;
    if first.kind != kind::HELLO {
        bail!("erwartet HELLO");
    }
    let hello: Hello = first.parse()?;
    if hello.token != shared.token {
        let _ = to_game.send(Frame::json(kind::ERROR, &ErrorMsg { message: "Ungültiges Token".into() })).await;
        bail!("ungültiges Token");
    }
    let game = hello.game.trim().to_lowercase();
    if game.is_empty() || game.len() > 64 {
        bail!("ungültige Spiel-ID");
    }
    to_game
        .send(Frame::json(
            kind::WELCOME,
            &Welcome { id: shared.endpoint.id().to_string(), name: shared.name.read().await.clone(), launch_action: None, launch_code: None },
        ))
        .await?;

    let mut role = Role::None;
    let err = |m: String| Frame::json(kind::ERROR, &ErrorMsg { message: m });

    loop {
        let frame = match read_frame(&mut rd).await {
            Ok(f) => f,
            Err(_) => break, // Spiel beendet oder Verbindung weg
        };
        match frame.kind {
            kind::HOST => {
                if shared.name.read().await.is_empty() {
                    to_game.send(err(NAME_REQUIRED.into())).await?;
                    shared.event(json!({ "event": "name_required", "game": game }));
                    continue;
                }
                if !matches!(role, Role::None) {
                    to_game.send(err("Bereits in einer Sitzung.".into())).await?;
                    continue;
                }
                let req: HostRequest = frame.parse().unwrap_or_default();
                let mut hosts = shared.hosts.lock().await;
                if hosts.contains_key(&game) {
                    to_game.send(err("Dieses Spiel wird auf diesem Rechner schon gehostet.".into())).await?;
                    continue;
                }
                let state = Arc::new(HostState {
                    game: game.clone(),
                    to_game: to_game.clone(),
                    peers: Mutex::new(HashMap::new()),
                    names: std::sync::Mutex::default(),
                    next_peer: AtomicU16::new(1),
                    max_peers: req.max_peers,
                });
                hosts.insert(game.clone(), state.clone());
                drop(hosts);
                role = Role::Host(state);
                let code = shared.endpoint.id().to_string();
                to_game.send(Frame::json(kind::HOSTED, &Hosted { code: code.clone() })).await?;
                shared.event(json!({ "event": "hosted", "game": game, "code": code }));
            }
            kind::JOIN => {
                if shared.name.read().await.is_empty() {
                    to_game.send(err(NAME_REQUIRED.into())).await?;
                    shared.event(json!({ "event": "name_required", "game": game }));
                    continue;
                }
                if !matches!(role, Role::None) {
                    to_game.send(err("Bereits in einer Sitzung.".into())).await?;
                    continue;
                }
                let req: JoinRequest = frame.parse()?;
                match join(&shared, &game, &req.code, to_game.clone()).await {
                    Ok((link, joined)) => {
                        role = Role::Client(link);
                        to_game.send(Frame::json(kind::JOINED, &joined)).await?;
                        to_game
                            .send(Frame::json(
                                kind::PEER_JOINED,
                                &PeerJoined { peer: HOST_PEER, id: joined.host_id.clone(), name: joined.host_name.clone() },
                            ))
                            .await?;
                        shared.event(json!({ "event": "joined", "game": game, "host": joined.host_name }));
                    }
                    Err(e) => to_game.send(err(format!("{e:#}"))).await?,
                }
            }
            kind::SEND => {
                let (to, reliable, data) = frame.split_data()?;
                let result = match &role {
                    Role::Host(h) => h.send(to, reliable, data).await,
                    Role::Client(link) => link.send(reliable, data).await, // Mitspieler senden immer an den Host
                    Role::None => Err(anyhow::anyhow!("Keine Sitzung – zuerst HOST oder JOIN.")),
                };
                if let Err(e) = result {
                    to_game.send(err(format!("{e:#}"))).await?;
                }
            }
            kind::KICK => {
                if let Role::Host(h) = &role {
                    let req: KickRequest = frame.parse()?;
                    if let Some(link) = h.peers.lock().await.get(&req.peer) {
                        link.close("Vom Host entfernt");
                    }
                }
            }
            kind::LEAVE => {
                end_role(&shared, &game, &mut role).await;
                to_game.send(Frame::json(kind::SESSION_ENDED, &SessionEnded { reason: "Verlassen".into() })).await?;
            }
            other => {
                to_game.send(err(format!("Unbekannter Rahmentyp {other}"))).await?;
            }
        }
    }
    end_role(&shared, &game, &mut role).await;
    Ok(())
}

async fn end_role(shared: &Arc<Shared>, game: &str, role: &mut Role) {
    match std::mem::replace(role, Role::None) {
        Role::Host(h) => {
            h.close_all("Host hat das Spiel beendet").await;
            let mut hosts = shared.hosts.lock().await;
            if hosts.get(game).is_some_and(|x| Arc::ptr_eq(x, &h)) {
                hosts.remove(game);
            }
            shared.event(json!({ "event": "ended", "game": game }));
        }
        Role::Client(link) => {
            link.close("Mitspieler hat verlassen");
            shared.event(json!({ "event": "ended", "game": game }));
        }
        Role::None => {}
    }
}

/// Tritt dem Spiel eines anderen Launchers bei. Der Code ist dessen Endpoint-ID.
async fn join(shared: &Arc<Shared>, game: &str, code: &str, to_game: mpsc::Sender<Frame>) -> Result<(PeerLink, Joined)> {
    let host: EndpointId = normalize_code(code).parse().map_err(|_| anyhow::anyhow!("Ungültiger Code."))?;
    if host == shared.endpoint.id() {
        bail!("Das ist dein eigener Code.");
    }
    let conn = tokio::time::timeout(Duration::from_secs(25), shared.endpoint.connect(host, ALPN))
        .await
        .context("Host nicht erreichbar (Zeitüberschreitung).")?
        .map_err(|e| anyhow::anyhow!("Verbindung fehlgeschlagen: {e}"))?;
    let (mut send, mut recv) = conn.open_bi().await.map_err(|e| anyhow::anyhow!("{e}"))?;
    let name = shared.name.read().await.clone();
    write_json(&mut send, &PeerHello { version: VERSION, game: game.to_string(), name }).await?;
    let welcome: PeerWelcome = tokio::time::timeout(Duration::from_secs(10), read_json(&mut recv))
        .await
        .context("Host antwortet nicht.")??;
    if !welcome.ok {
        conn.close(0u32.into(), b"abgelehnt");
        bail!("{}", if welcome.reason.is_empty() { "Beitritt abgelehnt." } else { &welcome.reason });
    }
    let host_id = host.to_string();
    let host_name = sanitize_name(&welcome.name, &host_id);
    let ended_tx = to_game.clone();
    let link = spawn_pumps(conn, send, recv, HOST_PEER, to_game, move |reason| {
        tokio::spawn(async move {
            let _ = ended_tx.send(Frame::json(kind::SESSION_ENDED, &SessionEnded { reason })).await;
        });
    });
    Ok((link, Joined { host_id, host_name }))
}

/// Codes dürfen mit Leerzeichen, Bindestrichen oder dem Präfix `mett:` eingefügt werden.
pub fn normalize_code(code: &str) -> String {
    let c = code.trim();
    let c = c.strip_prefix("mett://join/").or_else(|| c.strip_prefix("mett:")).unwrap_or(c);
    let c = c.rsplit('/').next().unwrap_or(c);
    c.chars().filter(|ch| ch.is_ascii_alphanumeric()).collect::<String>().to_lowercase()
}

#[cfg(test)]
mod tests {
    use super::normalize_code;

    #[test]
    fn normalizes_pasted_codes() {
        assert_eq!(normalize_code("  AB-cd 12\n"), "abcd12");
        assert_eq!(normalize_code("mett://join/kasse0/abcd"), "abcd");
    }
}
