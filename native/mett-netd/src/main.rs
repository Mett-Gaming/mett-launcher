//! `mett-netd` – Peer-to-Peer-Netzdienst des Mett Launchers.
//!
//! Der Launcher startet den Dienst einmal. Er hält die iroh-Identität (Schlüssel unter `--data-dir`)
//! und alle Verbindungen zu anderen Launchern. Spiele verbinden sich lokal per TCP (siehe
//! `mett-net-proto`) und können ein Spiel hosten oder per Code beitreten – ohne Account,
//! ohne eigenen Server. Verbindungen laufen direkt (Hole-Punching) oder, wenn das nicht klappt,
//! verschlüsselt über die Relays von n0.
//!
//! Kanal zum Launcher: stdout = JSON-Zeilen (Ereignisse), stdin = JSON-Zeilen (Befehle).
//! Schließt der Launcher stdin (z. B. weil er beendet wurde), beendet sich der Dienst.

mod peer;
mod session;

use std::{collections::HashMap, path::PathBuf, sync::Arc};

use anyhow::{Context, Result};
use iroh::{Endpoint, SecretKey, endpoint::presets};
use serde_json::json;
use tokio::{
    io::{AsyncBufReadExt, AsyncWriteExt, BufReader},
    net::TcpListener,
    sync::{Mutex, RwLock, mpsc},
};

/// ALPN aller Mett-Verbindungen; das Spiel steht im Handshake.
pub const ALPN: &[u8] = b"mett/net/1";

/// Gemeinsamer Zustand des Dienstes.
pub struct Shared {
    pub endpoint: Endpoint,
    pub token: String,
    pub name: RwLock<String>,
    /// Offene Spiele je Spiel-ID (nur ein Host pro Spiel und Rechner).
    pub hosts: Mutex<HashMap<String, Arc<session::HostState>>>,
    events: mpsc::UnboundedSender<serde_json::Value>,
}

impl Shared {
    /// Ereignis an den Launcher (stdout).
    pub fn event(&self, value: serde_json::Value) {
        let _ = self.events.send(value);
    }
}

struct Args {
    data_dir: PathBuf,
    name: String,
}

fn parse_args() -> Result<Args> {
    let mut data_dir = None;
    let mut name = None;
    let mut it = std::env::args().skip(1);
    while let Some(a) = it.next() {
        match a.as_str() {
            "--data-dir" => data_dir = it.next().map(PathBuf::from),
            "--name" => name = it.next(),
            "--version" => {
                println!("mett-netd {}", env!("CARGO_PKG_VERSION"));
                std::process::exit(0);
            }
            other => anyhow::bail!("Unbekanntes Argument {other}"),
        }
    }
    Ok(Args {
        data_dir: data_dir.context("--data-dir fehlt")?,
        name: name.unwrap_or_default(),
    })
}

/// Lädt den dauerhaften Schlüssel oder legt ihn an. Er ist die „Identität“ des Spielers.
fn load_or_create_key(dir: &std::path::Path) -> Result<SecretKey> {
    std::fs::create_dir_all(dir)?;
    let path = dir.join("secret.key");
    if let Ok(bytes) = std::fs::read(&path) {
        if let Ok(arr) = <[u8; 32]>::try_from(bytes.as_slice()) {
            return Ok(SecretKey::from_bytes(&arr));
        }
    }
    let key = SecretKey::generate();
    let tmp = path.with_extension("tmp");
    std::fs::write(&tmp, key.to_bytes())?;
    std::fs::rename(&tmp, &path)?;
    Ok(key)
}

fn default_name(id: &str) -> String {
    format!("Spieler-{}", &id[..4.min(id.len())].to_uppercase())
}

#[tokio::main]
async fn main() -> Result<()> {
    let args = parse_args()?;
    let key = load_or_create_key(&args.data_dir)?;

    let (ev_tx, mut ev_rx) = mpsc::unbounded_channel::<serde_json::Value>();
    // stdout-Schreiber: genau eine JSON-Zeile pro Ereignis.
    tokio::spawn(async move {
        let mut out = tokio::io::stdout();
        while let Some(v) = ev_rx.recv().await {
            let mut line = v.to_string();
            line.push('\n');
            if out.write_all(line.as_bytes()).await.is_err() {
                break;
            }
            let _ = out.flush().await;
        }
    });

    let endpoint = Endpoint::builder(presets::N0)
        .secret_key(key)
        .alpns(vec![ALPN.to_vec()])
        .bind()
        .await
        .map_err(|e| anyhow::anyhow!("iroh-Endpoint: {e}"))?;
    let id = endpoint.id().to_string();
    let name = if args.name.trim().is_empty() { default_name(&id) } else { args.name.trim().to_string() };

    let listener = TcpListener::bind(("127.0.0.1", 0)).await?;
    let addr = listener.local_addr()?;
    let token = hex(&rand::random::<[u8; 16]>());

    let shared = Arc::new(Shared {
        endpoint: endpoint.clone(),
        token: token.clone(),
        name: RwLock::new(name.clone()),
        hosts: Mutex::new(HashMap::new()),
        events: ev_tx,
    });

    shared.event(json!({ "event": "ready", "id": id, "name": name, "addr": addr.to_string(), "token": token }));

    // Online-Status (Relay erreichbar) melden, ohne den Start zu blockieren.
    {
        let shared = shared.clone();
        tokio::spawn(async move {
            shared.endpoint.online().await;
            shared.event(json!({ "event": "online" }));
        });
    }

    // Eingehende P2P-Verbindungen anderer Launcher.
    {
        let shared = shared.clone();
        tokio::spawn(async move {
            while let Some(incoming) = shared.endpoint.accept().await {
                let shared = shared.clone();
                tokio::spawn(async move {
                    if let Err(e) = peer::handle_incoming(shared, incoming).await {
                        eprintln!("eingehende Verbindung: {e:#}");
                    }
                });
            }
        });
    }

    // Lokale Spiele.
    {
        let shared = shared.clone();
        tokio::spawn(async move {
            loop {
                match listener.accept().await {
                    Ok((stream, _)) => {
                        let shared = shared.clone();
                        tokio::spawn(async move {
                            if let Err(e) = session::run(shared, stream).await {
                                eprintln!("Spielverbindung: {e:#}");
                            }
                        });
                    }
                    Err(e) => eprintln!("accept: {e}"),
                }
            }
        });
    }

    // Befehle vom Launcher; EOF = Launcher weg -> beenden.
    let mut lines = BufReader::new(tokio::io::stdin()).lines();
    while let Ok(Some(line)) = lines.next_line().await {
        let Ok(cmd) = serde_json::from_str::<serde_json::Value>(&line) else { continue };
        match cmd.get("cmd").and_then(|c| c.as_str()) {
            Some("name") => {
                if let Some(n) = cmd.get("name").and_then(|n| n.as_str()) {
                    let n = n.trim();
                    *shared.name.write().await = if n.is_empty() { default_name(&id) } else { n.chars().take(32).collect() };
                }
            }
            Some("quit") => break,
            _ => {}
        }
    }

    endpoint.close().await;
    Ok(())
}

pub fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}
