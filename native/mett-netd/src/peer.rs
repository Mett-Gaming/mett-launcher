//! Verbindungen zu anderen Launchern (iroh/QUIC).
//!
//! Pro Mitspieler gibt es eine QUIC-Verbindung mit
//! - einem bidirektionalen Stream für zuverlässige, geordnete Nachrichten (`u32 Länge | Daten`),
//! - QUIC-Datagrammen für unzuverlässige Nachrichten (z. B. Positionen).

use std::sync::Arc;

use anyhow::{Context, Result, bail};
use bytes::Bytes;
use iroh::endpoint::{Connection, RecvStream, SendStream};
use mett_net_proto::{Frame, MAX_FRAME, PeerHello, PeerWelcome, VERSION};
use serde::{Serialize, de::DeserializeOwned};
use tokio::sync::mpsc;

use crate::Shared;

/// Sendekanal zu einem Mitspieler.
#[derive(Clone)]
pub struct PeerLink {
    pub conn: Connection,
    reliable_tx: mpsc::Sender<Vec<u8>>,
}

impl PeerLink {
    pub async fn send(&self, reliable: bool, data: &[u8]) -> Result<()> {
        if reliable {
            self.reliable_tx.send(data.to_vec()).await.context("Verbindung geschlossen")?;
        } else {
            let max = self.conn.max_datagram_size().unwrap_or(0);
            if data.len() > max {
                bail!("Unzuverlässige Nachricht zu groß ({} > {max} Bytes) – zuverlässig senden", data.len());
            }
            self.conn
                .send_datagram(Bytes::copy_from_slice(data))
                .map_err(|e| anyhow::anyhow!("Datagramm: {e}"))?;
        }
        Ok(())
    }

    pub fn close(&self, reason: &str) {
        self.conn.close(0u32.into(), reason.as_bytes());
    }
}

pub async fn write_msg(send: &mut SendStream, data: &[u8]) -> Result<()> {
    send.write_all(&(data.len() as u32).to_le_bytes()).await.map_err(|e| anyhow::anyhow!("{e}"))?;
    send.write_all(data).await.map_err(|e| anyhow::anyhow!("{e}"))?;
    Ok(())
}

pub async fn read_msg(recv: &mut RecvStream) -> Result<Vec<u8>> {
    let mut len = [0u8; 4];
    recv.read_exact(&mut len).await.map_err(|e| anyhow::anyhow!("{e}"))?;
    let len = u32::from_le_bytes(len) as usize;
    if len > MAX_FRAME {
        bail!("Nachricht zu groß ({len} Bytes)");
    }
    let mut buf = vec![0u8; len];
    recv.read_exact(&mut buf).await.map_err(|e| anyhow::anyhow!("{e}"))?;
    Ok(buf)
}

pub async fn write_json<T: Serialize>(send: &mut SendStream, v: &T) -> Result<()> {
    write_msg(send, &serde_json::to_vec(v)?).await
}

pub async fn read_json<T: DeserializeOwned>(recv: &mut RecvStream) -> Result<T> {
    Ok(serde_json::from_slice(&read_msg(recv).await?)?)
}

/// Startet Lese-/Schreib-Tasks für eine fertige Verbindung. `on_close` läuft genau einmal,
/// sobald die Verbindung endet (Gegenseite weg, Netz weg, `close`).
pub fn spawn_pumps(
    conn: Connection,
    mut send: SendStream,
    mut recv: RecvStream,
    peer: u16,
    to_game: mpsc::Sender<Frame>,
    on_close: impl FnOnce(String) + Send + 'static,
) -> PeerLink {
    let (reliable_tx, mut reliable_rx) = mpsc::channel::<Vec<u8>>(1024);

    // zuverlässig: Spiel -> Gegenseite
    tokio::spawn(async move {
        while let Some(data) = reliable_rx.recv().await {
            if write_msg(&mut send, &data).await.is_err() {
                break;
            }
        }
        let _ = send.finish();
    });

    // zuverlässig: Gegenseite -> Spiel
    {
        let to_game = to_game.clone();
        tokio::spawn(async move {
            while let Ok(data) = read_msg(&mut recv).await {
                if to_game.send(Frame::recv(peer, true, &data)).await.is_err() {
                    break;
                }
            }
        });
    }

    // unzuverlässig: Datagramme -> Spiel
    {
        let conn = conn.clone();
        let to_game = to_game.clone();
        tokio::spawn(async move {
            while let Ok(data) = conn.read_datagram().await {
                // Datagramme dürfen verloren gehen – bei vollem Puffer verwerfen statt blockieren.
                let _ = to_game.try_send(Frame::recv(peer, false, &data));
            }
        });
    }

    // Ende der Verbindung beobachten.
    {
        let conn = conn.clone();
        tokio::spawn(async move {
            let reason = conn.closed().await;
            on_close(reason.to_string());
        });
    }

    PeerLink { conn, reliable_tx }
}

/// Eingehende Verbindung eines anderen Launchers, der einem hier gehosteten Spiel beitreten will.
pub async fn handle_incoming(shared: Arc<Shared>, incoming: iroh::endpoint::Incoming) -> Result<()> {
    let accepting = incoming.accept().map_err(|e| anyhow::anyhow!("{e}"))?;
    let conn = accepting.await.map_err(|e| anyhow::anyhow!("{e}"))?;
    let (mut send, mut recv) = conn.accept_bi().await.map_err(|e| anyhow::anyhow!("{e}"))?;
    let hello: PeerHello = tokio::time::timeout(std::time::Duration::from_secs(10), read_json(&mut recv))
        .await
        .context("Handshake-Timeout")??;

    let reject = |reason: &str| PeerWelcome { ok: false, name: String::new(), reason: reason.to_string() };
    if hello.version != VERSION {
        write_json(&mut send, &reject("Unterschiedliche Launcher-Versionen – bitte beide aktualisieren.")).await?;
        let _ = send.finish();
        conn.closed().await;
        return Ok(());
    }
    let host = shared.hosts.lock().await.get(&hello.game).cloned();
    let Some(host) = host else {
        write_json(&mut send, &reject("Hier ist gerade kein offenes Spiel.")).await?;
        let _ = send.finish();
        conn.closed().await;
        return Ok(());
    };
    host.add_peer(&shared, conn, send, recv, hello.name).await
}
