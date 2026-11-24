//! Verbindung zum Launcher (nur localhost): Bilder und Rechtecke empfangen, Messwerte melden.
//!
//! Erste Zeile der Schicht: `{"token":"…"}`. Danach schickt der Launcher Pakete: Laenge (u32, LE),
//! Art (u8), Inhalt. Die Schicht meldet zweimal je Sekunde eine JSON-Zeile mit Bildrate und Groesse.

use crate::scene::{MAX_QUADS, MAX_SIZE, Quad, Scene};
use std::io::{Read, Write};
use std::net::{SocketAddr, TcpStream};
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

pub const ADDRESS: &str = "METT_OVERLAY_ADDR";
pub const TOKEN: &str = "METT_OVERLAY_TOKEN";

const KIND_IMAGE: u8 = 1;
const KIND_QUADS: u8 = 2;
const KIND_REMOVE: u8 = 3;
const KIND_CLEAR: u8 = 4;
/// Bytes je Rechteck
const QUAD_BYTES: usize = 4 + 1 + 10 * 4;
const MAX_PACKET: usize = (MAX_SIZE as usize * MAX_SIZE as usize * 4) / 4;
const REPORT: Duration = Duration::from_millis(500);

/// Gemeinsamer Zustand.
#[derive(Default)]
pub struct Shared {
    pub scene: Mutex<Scene>,
    /// gezeigte Bilder seit dem Start
    pub frames: AtomicU64,
    pub width: AtomicU32,
    pub height: AtomicU32,
}

static SHARED: OnceLock<&'static Shared> = OnceLock::new();

/// Zustand; baut beim ersten Mal die Verbindung auf (im eigenen Faden). `None` = ohne Launcher.
pub fn shared() -> Option<&'static Shared> {
    static TRIED: OnceLock<bool> = OnceLock::new();
    let on = *TRIED.get_or_init(|| {
        let (Ok(addr), Ok(token)) = (std::env::var(ADDRESS), std::env::var(TOKEN)) else {
            return false;
        };
        let Some(addr) = addr
            .parse::<SocketAddr>()
            .ok()
            .filter(|a| a.ip().is_loopback())
        else {
            return false;
        };
        let state: &'static Shared = Box::leak(Box::default());
        let started = std::thread::Builder::new()
            .name("mett-overlay".into())
            .spawn(move || run(addr, &token, state));
        started.is_ok() && SHARED.set(state).is_ok()
    });
    if on { SHARED.get().copied() } else { None }
}

fn run(addr: SocketAddr, token: &str, state: &'static Shared) {
    let Ok(mut stream) = TcpStream::connect_timeout(&addr, Duration::from_secs(2)) else {
        return;
    };
    let _ = stream.set_nodelay(true);
    let clean: String = token.chars().filter(char::is_ascii_alphanumeric).collect();
    if writeln!(stream, "{{\"token\":\"{clean}\"}}").is_err() {
        return;
    }
    if let Ok(out) = stream.try_clone() {
        let _ = std::thread::Builder::new()
            .name("mett-overlay-fps".into())
            .spawn(move || report(out, state));
    }
    receive(&mut stream, state);
    // Launcher weg: nichts bleibt stehen
    if let Ok(mut s) = state.scene.lock() {
        s.clear();
    }
}

fn report(mut out: TcpStream, state: &Shared) {
    let (mut last, mut at) = (state.frames.load(Ordering::Relaxed), Instant::now());
    loop {
        std::thread::sleep(REPORT);
        let frames = state.frames.load(Ordering::Relaxed);
        let seconds = at.elapsed().as_secs_f32();
        let fps = (frames - last) as f32 / seconds.max(0.001);
        (last, at) = (frames, Instant::now());
        let (w, h) = (
            state.width.load(Ordering::Relaxed),
            state.height.load(Ordering::Relaxed),
        );
        if writeln!(out, "{{\"fps\":{fps:.1},\"w\":{w},\"h\":{h}}}").is_err() {
            return;
        }
    }
}

fn receive(stream: &mut impl Read, state: &Shared) {
    let mut head = [0u8; 5];
    let mut body = Vec::new();
    while stream.read_exact(&mut head).is_ok() {
        let len = u32::from_le_bytes([head[0], head[1], head[2], head[3]]) as usize;
        if len > MAX_PACKET {
            return;
        }
        body.resize(len, 0);
        if stream.read_exact(&mut body).is_err() {
            return;
        }
        let Ok(mut scene) = state.scene.lock() else {
            return;
        };
        apply(&mut scene, head[4], &body);
    }
}

/// Ein Paket anwenden; kaputte fallen weg.
pub fn apply(scene: &mut Scene, kind: u8, body: &[u8]) {
    let num = |at: usize| {
        let b = body.get(at..at + 4)?;
        Some(u32::from_le_bytes(b.try_into().ok()?))
    };
    match kind {
        KIND_IMAGE => {
            let (Some(id), Some(w), Some(h)) = (num(0), num(4), num(8)) else {
                return;
            };
            let ok = (1..=MAX_SIZE).contains(&w) && (1..=MAX_SIZE).contains(&h);
            let size = w as usize * h as usize * 4;
            if ok && body.len() == 12 + size {
                scene.set_image(id, w, h, body[12..].to_vec());
            }
        }
        KIND_QUADS => {
            let list = body.chunks_exact(QUAD_BYTES).take(MAX_QUADS);
            let quads: Vec<Quad> = list.filter_map(quad).collect();
            scene.set_quads(quads, Instant::now());
        }
        KIND_REMOVE => {
            if let Some(id) = num(0) {
                scene.remove_image(id);
            }
        }
        KIND_CLEAR => scene.clear(),
        _ => {}
    }
}

fn quad(b: &[u8]) -> Option<Quad> {
    let image = u32::from_le_bytes(b[..4].try_into().ok()?);
    let mut v = [0.0f32; 10];
    for (k, out) in v.iter_mut().enumerate() {
        let at = 5 + k * 4;
        *out = f32::from_le_bytes(b[at..at + 4].try_into().ok()?);
    }
    if v.iter().any(|x| !x.is_finite() || x.abs() > 1e6) {
        return None;
    }
    let [x, y, w, h, alpha, fade_in, hold, fade_out, slide_x, slide_y] = v;
    Some(Quad {
        image,
        anchor: b[4],
        x,
        y,
        w,
        h,
        alpha,
        fade_in: fade_in.max(0.0),
        hold,
        fade_out: fade_out.max(0.0),
        slide_x,
        slide_y,
    })
}

/// Rechteck als Paket-Inhalt (fuer Pruefprogramme).
pub fn encode(q: &Quad) -> Vec<u8> {
    let mut out = q.image.to_le_bytes().to_vec();
    out.push(q.anchor);
    for v in [
        q.x, q.y, q.w, q.h, q.alpha, q.fade_in, q.hold, q.fade_out, q.slide_x, q.slide_y,
    ] {
        out.extend(v.to_le_bytes());
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn packets_survive_the_trip_and_bad_ones_are_dropped() {
        let q = crate::scene::tests::quad();
        assert_eq!(encode(&q).len(), QUAD_BYTES);
        assert_eq!(quad(&encode(&q)), Some(q));
        let mut s = Scene::default();
        let mut image = Vec::new();
        for v in [1u32, 2, 1] {
            image.extend(v.to_le_bytes());
        }
        apply(&mut s, KIND_IMAGE, &image);
        assert!(s.images.is_empty(), "Bildpunkte fehlen");
        image.extend([7; 8]);
        apply(&mut s, KIND_IMAGE, &image);
        assert_eq!(s.images[&1].rgba.len(), 8);
        apply(&mut s, KIND_QUADS, &encode(&q));
        assert!(!s.is_empty());
        apply(&mut s, KIND_QUADS, &[1, 2, 3]);
        assert!(s.is_empty());
        let bad = Quad { x: f32::NAN, ..q };
        assert_eq!(quad(&encode(&bad)), None);
        apply(&mut s, KIND_REMOVE, &1u32.to_le_bytes());
        assert!(s.images.is_empty());
    }
}
