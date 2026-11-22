//! Lokales Protokoll zwischen einem Spiel und dem Netzdienst `mett-netd` des Mett Launchers.
//!
//! Transport: TCP auf `127.0.0.1` (Adresse in `METT_NET_ADDR`). Jeder Rahmen ist
//! `u32 Länge (LE, ohne die 4 Längenbytes) | u8 Typ | Nutzdaten`.
//! Steuerrahmen tragen JSON, Datenrahmen Binärdaten. Siehe `PROTOCOL.md`.

use serde::{Deserialize, Serialize};
use std::io::{self, Read, Write};

/// Höchste Rahmengröße (Typbyte + Nutzdaten).
pub const MAX_FRAME: usize = 4 * 1024 * 1024;
/// Peer-Index des Hosts aus Sicht eines beigetretenen Spielers.
pub const HOST_PEER: u16 = 0;
/// Ziel „an alle“ beim Senden.
pub const BROADCAST: u16 = u16::MAX;
/// Protokollversion; steht im `Hello`.
pub const VERSION: u32 = 1;

pub mod kind {
    // Spiel -> Dienst
    pub const HELLO: u8 = 1;
    pub const HOST: u8 = 2;
    pub const JOIN: u8 = 3;
    pub const SEND: u8 = 4;
    pub const LEAVE: u8 = 5;
    pub const KICK: u8 = 6;
    // Dienst -> Spiel
    pub const WELCOME: u8 = 101;
    pub const HOSTED: u8 = 102;
    pub const JOINED: u8 = 103;
    pub const PEER_JOINED: u8 = 104;
    pub const PEER_LEFT: u8 = 105;
    pub const RECV: u8 = 106;
    pub const ERROR: u8 = 107;
    pub const SESSION_ENDED: u8 = 108;
}

/// Bit 0 im Flags-Byte von SEND: zuverlässig und geordnet (sonst: unzuverlässiges Datagramm).
pub const FLAG_RELIABLE: u8 = 1;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Hello {
    pub version: u32,
    pub token: String,
    pub game: String,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct HostRequest {
    /// Höchstzahl an Mitspielern (ohne Host); 0 = unbegrenzt.
    #[serde(default)]
    pub max_peers: u16,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct JoinRequest {
    pub code: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct KickRequest {
    pub peer: u16,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Welcome {
    /// Eigene Endpoint-ID (öffentlicher Schlüssel).
    pub id: String,
    pub name: String,
    /// Vom Launcher vorgegebene Aktion beim Start: `host`, `join` oder leer.
    #[serde(default)]
    pub launch_action: Option<String>,
    /// Bei `join`: der Code, dem beigetreten werden soll.
    #[serde(default)]
    pub launch_code: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Hosted {
    /// Code, den Mitspieler zum Beitreten brauchen.
    pub code: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Joined {
    pub host_id: String,
    pub host_name: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PeerJoined {
    pub peer: u16,
    pub id: String,
    pub name: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PeerLeft {
    pub peer: u16,
    #[serde(default)]
    pub reason: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ErrorMsg {
    pub message: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SessionEnded {
    #[serde(default)]
    pub reason: String,
}

/// Handshake zwischen zwei Diensten über iroh (erste Nachricht auf dem zuverlässigen Stream).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PeerHello {
    pub version: u32,
    pub game: String,
    pub name: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PeerWelcome {
    pub ok: bool,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub reason: String,
}

/// Ein decodierter Rahmen.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Frame {
    pub kind: u8,
    pub payload: Vec<u8>,
}

impl Frame {
    pub fn json<T: Serialize>(kind: u8, value: &T) -> Self {
        Self { kind, payload: serde_json::to_vec(value).expect("serialisierbar") }
    }

    pub fn parse<T: for<'de> Deserialize<'de>>(&self) -> io::Result<T> {
        serde_json::from_slice(&self.payload).map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))
    }

    /// SEND: `flags | u16 Ziel | Daten`.
    pub fn send(to: u16, reliable: bool, data: &[u8]) -> Self {
        let mut p = Vec::with_capacity(3 + data.len());
        p.push(if reliable { FLAG_RELIABLE } else { 0 });
        p.extend_from_slice(&to.to_le_bytes());
        p.extend_from_slice(data);
        Self { kind: kind::SEND, payload: p }
    }

    /// RECV: `flags | u16 Absender | Daten`.
    pub fn recv(from: u16, reliable: bool, data: &[u8]) -> Self {
        let mut f = Self::send(from, reliable, data);
        f.kind = kind::RECV;
        f
    }

    /// Zerlegt SEND/RECV in (Peer, zuverlässig, Daten).
    pub fn split_data(&self) -> io::Result<(u16, bool, &[u8])> {
        if self.payload.len() < 3 {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "Datenrahmen zu kurz"));
        }
        let reliable = self.payload[0] & FLAG_RELIABLE != 0;
        let peer = u16::from_le_bytes([self.payload[1], self.payload[2]]);
        Ok((peer, reliable, &self.payload[3..]))
    }

    pub fn encode(&self) -> Vec<u8> {
        let len = (self.payload.len() + 1) as u32;
        let mut out = Vec::with_capacity(5 + self.payload.len());
        out.extend_from_slice(&len.to_le_bytes());
        out.push(self.kind);
        out.extend_from_slice(&self.payload);
        out
    }

    pub fn write_to(&self, w: &mut impl Write) -> io::Result<()> {
        w.write_all(&self.encode())
    }

    /// Liest genau einen Rahmen (blockierend).
    pub fn read_from(r: &mut impl Read) -> io::Result<Self> {
        let mut len = [0u8; 4];
        r.read_exact(&mut len)?;
        let len = u32::from_le_bytes(len) as usize;
        if len == 0 || len > MAX_FRAME {
            return Err(io::Error::new(io::ErrorKind::InvalidData, format!("ungültige Rahmenlänge {len}")));
        }
        let mut buf = vec![0u8; len];
        r.read_exact(&mut buf)?;
        let kind = buf[0];
        buf.remove(0);
        Ok(Self { kind, payload: buf })
    }

    /// Dekodiert aus einem bereits vollständig gelesenen Puffer `u8 Typ | Nutzdaten`.
    pub fn from_body(mut body: Vec<u8>) -> io::Result<Self> {
        if body.is_empty() {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "leerer Rahmen"));
        }
        let kind = body.remove(0);
        Ok(Self { kind, payload: body })
    }
}

/// Umgebungsvariablen, die der Launcher einem Spiel mitgibt.
pub mod env {
    pub const ADDR: &str = "METT_NET_ADDR";
    pub const TOKEN: &str = "METT_NET_TOKEN";
    pub const GAME: &str = "METT_NET_GAME";
    pub const ACTION: &str = "METT_NET_ACTION";
    pub const CODE: &str = "METT_NET_CODE";
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roundtrip_data_frame() {
        let f = Frame::send(7, true, b"hallo");
        let bytes = f.encode();
        let back = Frame::read_from(&mut &bytes[..]).unwrap();
        assert_eq!(back, f);
        let (peer, reliable, data) = back.split_data().unwrap();
        assert_eq!((peer, reliable, data), (7, true, &b"hallo"[..]));
    }

    #[test]
    fn roundtrip_json_frame() {
        let f = Frame::json(kind::JOIN, &JoinRequest { code: "abc".into() });
        let back = Frame::read_from(&mut &f.encode()[..]).unwrap();
        assert_eq!(back.parse::<JoinRequest>().unwrap().code, "abc");
    }

    #[test]
    fn rejects_oversized_frames() {
        let mut bytes = ((MAX_FRAME + 1) as u32).to_le_bytes().to_vec();
        bytes.push(1);
        assert!(Frame::read_from(&mut &bytes[..]).is_err());
    }
}
