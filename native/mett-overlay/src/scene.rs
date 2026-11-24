//! Was gezeigt wird: Bilder und Rechtecke vom Launcher, mit einfachen Bewegungen.

use std::collections::HashMap;
use std::sync::Arc;
use std::time::Instant;

/// Hoechste Kantenlaenge eines Bildes
pub const MAX_SIZE: u32 = 4096;
/// Hoechste Anzahl Rechtecke
pub const MAX_QUADS: usize = 64;
/// Hoechste Anzahl Bilder
pub const MAX_IMAGES: usize = 64;
/// Groessen sind fuer diese Fensterhoehe gedacht
const BASE_HEIGHT: f32 = 1080.0;

/// Ein Bild, RGBA vormultipliziert.
pub struct Image {
    pub width: u32,
    pub height: u32,
    pub rgba: Vec<u8>,
    /// aendert sich mit dem Inhalt
    pub version: u64,
}

/// Ein Rechteck (Laengen in Einheiten bei 1080 Zeilen).
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Quad {
    pub image: u32,
    /// 0 links oben, 1 rechts oben, 2 links unten, 3 rechts unten, 4 Mitte
    pub anchor: u8,
    /// Abstand vom Anker nach innen (Mitte: nach rechts und unten)
    pub x: f32,
    pub y: f32,
    pub w: f32,
    pub h: f32,
    pub alpha: f32,
    /// Sekunden; `hold` < 0 = bleibt
    pub fade_in: f32,
    pub hold: f32,
    pub fade_out: f32,
    /// Verschiebung am Anfang und Ende
    pub slide_x: f32,
    pub slide_y: f32,
}

/// Fertig zum Zeichnen.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Placed {
    pub image: u32,
    /// links, oben, Breite, Hoehe in -1..1
    pub rect: [f32; 4],
    pub alpha: f32,
}

/// Aktueller Stand.
#[derive(Default)]
pub struct Scene {
    pub images: HashMap<u32, Arc<Image>>,
    quads: Vec<(Quad, Instant)>,
    version: u64,
}

impl Scene {
    pub fn set_image(&mut self, id: u32, width: u32, height: u32, rgba: Vec<u8>) {
        if self.images.len() >= MAX_IMAGES && !self.images.contains_key(&id) {
            return;
        }
        self.version += 1;
        let version = self.version;
        let image = Image {
            width,
            height,
            rgba,
            version,
        };
        self.images.insert(id, Arc::new(image));
    }

    pub fn remove_image(&mut self, id: u32) {
        self.images.remove(&id);
        self.quads.retain(|q| q.0.image != id);
    }

    /// Neue Rechtecke. Unveraenderte laufen weiter, neue beginnen jetzt.
    pub fn set_quads(&mut self, new: Vec<Quad>, now: Instant) {
        let old = std::mem::take(&mut self.quads);
        self.quads = new
            .into_iter()
            .take(MAX_QUADS)
            .map(|q| {
                let since = old.iter().find(|o| o.0 == q).map_or(now, |o| o.1);
                (q, since)
            })
            .collect();
    }

    pub fn clear(&mut self) {
        self.images.clear();
        self.quads.clear();
    }

    pub fn is_empty(&self) -> bool {
        self.quads.is_empty()
    }

    /// Rechtecke zum Zeitpunkt `now` fuer ein Fenster; Abgelaufenes faellt weg.
    pub fn place(&mut self, (w, h): (u32, u32), now: Instant) -> Vec<Placed> {
        self.quads
            .retain(|(q, t)| strength(q, (now - *t).as_secs_f32()).is_some());
        let images = &self.images;
        self.quads
            .iter()
            .filter(|(q, _)| images.contains_key(&q.image))
            .filter_map(|(q, t)| {
                let k = strength(q, (now - *t).as_secs_f32())?;
                Some(place(q, k, (w as f32, h as f32)))
            })
            .collect()
    }
}

/// Sichtbarkeit 0..1; `None` = vorbei.
fn strength(q: &Quad, t: f32) -> Option<f32> {
    if t < q.fade_in {
        return Some(t / q.fade_in);
    }
    if q.hold < 0.0 {
        return Some(1.0);
    }
    let rest = t - q.fade_in - q.hold;
    if rest < 0.0 {
        Some(1.0)
    } else if rest < q.fade_out {
        Some(1.0 - rest / q.fade_out)
    } else {
        None
    }
}

fn place(q: &Quad, k: f32, (w, h): (f32, f32)) -> Placed {
    // weich anlaufen und auslaufen
    let k = k * k * (3.0 - 2.0 * k);
    let s = h / BASE_HEIGHT;
    let (qw, qh) = (q.w * s, q.h * s);
    let off = (q.slide_x * s * (1.0 - k), q.slide_y * s * (1.0 - k));
    let (x, y) = (q.x * s, q.y * s);
    let left = match q.anchor {
        1 | 3 => w - x - qw,
        4 => (w - qw) * 0.5 + x,
        _ => x,
    } + off.0;
    let top = match q.anchor {
        2 | 3 => h - y - qh,
        4 => (h - qh) * 0.5 + y,
        _ => y,
    } + off.1;
    // auf ganze Bildpunkte, damit Schrift scharf bleibt
    let (left, top) = (left.round(), top.round());
    Placed {
        image: q.image,
        rect: [
            left / w * 2.0 - 1.0,
            top / h * 2.0 - 1.0,
            qw.round() / w * 2.0,
            qh.round() / h * 2.0,
        ],
        alpha: (q.alpha * k).clamp(0.0, 1.0),
    }
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use std::time::Duration;

    pub fn quad() -> Quad {
        Quad {
            image: 1,
            anchor: 3,
            x: 20.0,
            y: 10.0,
            w: 100.0,
            h: 50.0,
            alpha: 1.0,
            fade_in: 1.0,
            hold: 2.0,
            fade_out: 1.0,
            slide_x: 0.0,
            slide_y: 60.0,
        }
    }

    #[test]
    fn toasts_come_and_go_and_sit_in_their_corner() {
        let mut s = Scene::default();
        let t = Instant::now();
        s.set_quads(vec![quad()], t);
        assert!(s.place((1920, 1080), t).is_empty(), "noch kein Bild");
        s.set_image(1, 1, 1, vec![255; 4]);
        let p = s.place((1920, 1080), t + Duration::from_secs(2));
        assert_eq!(p[0].alpha, 1.0);
        assert!((p[0].rect[0] - (1800.0 / 960.0 - 1.0)).abs() < 1e-5);
        assert!((p[0].rect[1] - (1020.0 / 540.0 - 1.0)).abs() < 1e-5);
        let p = s.place((3840, 2160), t + Duration::from_secs(2));
        assert!((p[0].rect[2] - 200.0 / 1920.0).abs() < 1e-5, "waechst mit");
        assert!(s.place((1920, 1080), t + Duration::from_secs(5)).is_empty());
        assert!(s.is_empty());
    }

    #[test]
    fn unchanged_quads_keep_running() {
        let mut s = Scene::default();
        let t = Instant::now();
        s.set_image(1, 1, 1, vec![255; 4]);
        s.set_quads(vec![quad()], t);
        let later = t + Duration::from_secs(2);
        let more = Quad { x: 500.0, ..quad() };
        s.set_quads(vec![quad(), more], later);
        let p = s.place((1920, 1080), later);
        assert_eq!((p[0].alpha, p[1].alpha), (1.0, 0.0));
    }
}
