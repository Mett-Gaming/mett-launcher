//! Prueft die Schicht auf einer echten Grafikkarte, mit unsichtbarem Fenster: spielt den Launcher,
//! laesst eine blaue Flaeche anzeigen und liest nach, ob das Overlay an der richtigen Stelle liegt.
//!
//! `cargo build --release -p mett-overlay && cargo run --release -p mett-overlay --example probe`
//! Wahlweise `METT_PROBE_GPU=<Nummer>`.

use ash::vk;
use mett_overlay::link::encode;
use mett_overlay::scene::Quad;
use raw_window_handle::{HasDisplayHandle, HasWindowHandle};
use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::time::{Duration, Instant};
use winit::application::ApplicationHandler;
use winit::event::WindowEvent;
use winit::event_loop::{ActiveEventLoop, EventLoop};
use winit::window::{Window, WindowId};

type R<T> = Result<T, Box<dyn std::error::Error>>;

const W: u32 = 1280;
const H: u32 = 720;

fn packet(out: &mut TcpStream, kind: u8, body: &[u8]) -> R<()> {
    out.write_all(&(body.len() as u32).to_le_bytes())?;
    out.write_all(&[kind])?;
    out.write_all(body)?;
    Ok(())
}

fn image(color: [u8; 4]) -> Vec<u8> {
    let mut b = Vec::new();
    for v in [1u32, 8, 8] {
        b.extend(v.to_le_bytes());
    }
    b.extend(color.repeat(64));
    b
}

fn quad(x: f32) -> Quad {
    Quad {
        image: 1,
        anchor: 3,
        x,
        y: 30.0,
        w: 300.0,
        h: 90.0,
        alpha: 1.0,
        fade_in: 0.0,
        hold: -1.0,
        fade_out: 0.0,
        slide_x: 0.0,
        slide_y: 0.0,
    }
}

struct Gpu {
    instance: ash::Instance,
    surface: vk::SurfaceKHR,
    surfaces: ash::khr::surface::Instance,
    physical: vk::PhysicalDevice,
    d: ash::Device,
    swap: ash::khr::swapchain::Device,
    queue: vk::Queue,
    pool: vk::CommandPool,
    cmd: vk::CommandBuffer,
    acquired: vk::Semaphore,
    fence: vk::Fence,
    sc: vk::SwapchainKHR,
    images: Vec<vk::Image>,
    done: Vec<vk::Semaphore>,
    seen: Vec<bool>,
    buffer: vk::Buffer,
    memory: vk::DeviceMemory,
    map: *const u8,
    _entry: ash::Entry,
}

impl Gpu {
    unsafe fn new(window: &Window) -> R<Self> {
        unsafe {
            let entry = ash::Entry::load()?;
            let (dh, wh) = (
                window.display_handle()?.as_raw(),
                window.window_handle()?.as_raw(),
            );
            let app = vk::ApplicationInfo::default().api_version(vk::API_VERSION_1_1);
            let ext = ash_window::enumerate_required_extensions(dh)?;
            let info = vk::InstanceCreateInfo::default()
                .application_info(&app)
                .enabled_extension_names(ext);
            let instance = entry.create_instance(&info, None)?;
            let surfaces = ash::khr::surface::Instance::new(&entry, &instance);
            let surface = ash_window::create_surface(&entry, &instance, dh, wh, None)?;
            let wish = std::env::var("METT_PROBE_GPU").ok();
            let wish: usize = wish.and_then(|s| s.parse().ok()).unwrap_or(0);
            let list = instance.enumerate_physical_devices()?;
            let physical = *list.get(wish).ok_or("keine Karte")?;
            let props = instance.get_physical_device_properties(physical);
            println!("{:?}", props.device_name_as_c_str()?);
            let queue_info = [vk::DeviceQueueCreateInfo::default().queue_priorities(&[1.0])];
            let names = [ash::khr::swapchain::NAME.as_ptr()];
            let info = vk::DeviceCreateInfo::default()
                .queue_create_infos(&queue_info)
                .enabled_extension_names(&names);
            let d = instance.create_device(physical, &info, None)?;
            let queue = d.get_device_queue(0, 0);
            let info = vk::CommandPoolCreateInfo::default()
                .flags(vk::CommandPoolCreateFlags::RESET_COMMAND_BUFFER);
            let pool = d.create_command_pool(&info, None)?;
            let info = vk::CommandBufferAllocateInfo::default()
                .command_pool(pool)
                .command_buffer_count(1);
            let cmd = d.allocate_command_buffers(&info)?[0];
            let signaled = vk::FenceCreateInfo::default().flags(vk::FenceCreateFlags::SIGNALED);
            let info = vk::BufferCreateInfo::default()
                .size((W * H * 8) as u64)
                .usage(vk::BufferUsageFlags::TRANSFER_DST);
            let buffer = d.create_buffer(&info, None)?;
            let req = d.get_buffer_memory_requirements(buffer);
            let types = instance.get_physical_device_memory_properties(physical);
            let want =
                vk::MemoryPropertyFlags::HOST_VISIBLE | vk::MemoryPropertyFlags::HOST_COHERENT;
            let ty = (0..types.memory_type_count)
                .find(|&i| {
                    req.memory_type_bits & (1 << i) != 0
                        && types.memory_types[i as usize].property_flags.contains(want)
                })
                .ok_or("kein Speicher")?;
            let info = vk::MemoryAllocateInfo::default()
                .allocation_size(req.size)
                .memory_type_index(ty);
            let memory = d.allocate_memory(&info, None)?;
            d.bind_buffer_memory(buffer, memory, 0)?;
            let map = d.map_memory(memory, 0, vk::WHOLE_SIZE, Default::default())?;
            Ok(Self {
                swap: ash::khr::swapchain::Device::new(&instance, &d),
                acquired: d.create_semaphore(&Default::default(), None)?,
                fence: d.create_fence(&signaled, None)?,
                surface,
                surfaces,
                physical,
                queue,
                pool,
                cmd,
                sc: vk::SwapchainKHR::null(),
                images: Vec::new(),
                done: Vec::new(),
                seen: Vec::new(),
                buffer,
                memory,
                map: map.cast(),
                d,
                instance,
                _entry: entry,
            })
        }
    }

    unsafe fn rebuild(&mut self) -> R<()> {
        unsafe {
            self.d.device_wait_idle()?;
            let caps = self
                .surfaces
                .get_physical_device_surface_capabilities(self.physical, self.surface)?;
            let info = vk::SwapchainCreateInfoKHR::default()
                .surface(self.surface)
                .min_image_count(caps.min_image_count + 1)
                .image_format(vk::Format::B8G8R8A8_UNORM)
                .image_extent(caps.current_extent)
                .image_array_layers(1)
                // absichtlich ohne Farbziel: das ergaenzt die Schicht
                .image_usage(vk::ImageUsageFlags::TRANSFER_SRC | vk::ImageUsageFlags::TRANSFER_DST)
                .pre_transform(caps.current_transform)
                .composite_alpha(vk::CompositeAlphaFlagsKHR::OPAQUE)
                .present_mode(vk::PresentModeKHR::IMMEDIATE)
                .old_swapchain(self.sc);
            let new = self.swap.create_swapchain(&info, None)?;
            self.swap.destroy_swapchain(self.sc, None);
            self.sc = new;
            self.images = self.swap.get_swapchain_images(new)?;
            for s in self.done.drain(..) {
                self.d.destroy_semaphore(s, None);
            }
            for _ in &self.images {
                self.done
                    .push(self.d.create_semaphore(&Default::default(), None)?);
            }
            self.seen = vec![false; self.images.len()];
            Ok(())
        }
    }

    /// Ein blaues Bild; liefert, was zuletzt in diesem Swapchain-Bild angezeigt wurde.
    unsafe fn frame(&mut self, read: bool) -> R<Option<Vec<[u8; 4]>>> {
        unsafe {
            let d = &self.d;
            d.wait_for_fences(&[self.fence], true, u64::MAX)?;
            d.reset_fences(&[self.fence])?;
            let (i, _) = self.swap.acquire_next_image(
                self.sc,
                u64::MAX,
                self.acquired,
                vk::Fence::null(),
            )?;
            let i = i as usize;
            let (image, cmd) = (self.images[i], self.cmd);
            d.begin_command_buffer(cmd, &Default::default())?;
            use vk::{AccessFlags as A, ImageLayout as L, PipelineStageFlags as S};
            let range = vk::ImageSubresourceRange {
                aspect_mask: vk::ImageAspectFlags::COLOR,
                base_mip_level: 0,
                level_count: 1,
                base_array_layer: 0,
                layer_count: 1,
            };
            let change = |from: L, to: L| {
                let b = [vk::ImageMemoryBarrier::default()
                    .image(image)
                    .old_layout(from)
                    .new_layout(to)
                    .src_access_mask(A::MEMORY_WRITE)
                    .dst_access_mask(A::MEMORY_READ | A::MEMORY_WRITE)
                    .subresource_range(range)];
                let all = S::ALL_COMMANDS;
                d.cmd_pipeline_barrier(cmd, all, all, Default::default(), &[], &[], &b);
            };
            let old = self.seen[i];
            if old {
                change(L::PRESENT_SRC_KHR, L::TRANSFER_SRC_OPTIMAL);
                let region = vk::BufferImageCopy::default()
                    .image_subresource(vk::ImageSubresourceLayers {
                        aspect_mask: vk::ImageAspectFlags::COLOR,
                        mip_level: 0,
                        base_array_layer: 0,
                        layer_count: 1,
                    })
                    .image_extent(vk::Extent3D {
                        width: W,
                        height: H,
                        depth: 1,
                    });
                let src = L::TRANSFER_SRC_OPTIMAL;
                d.cmd_copy_image_to_buffer(cmd, image, src, self.buffer, &[region]);
            }
            change(L::UNDEFINED, L::TRANSFER_DST_OPTIMAL);
            let blue = vk::ClearColorValue {
                float32: [0.0, 0.0, 1.0, 1.0],
            };
            d.cmd_clear_color_image(cmd, image, L::TRANSFER_DST_OPTIMAL, &blue, &[range]);
            change(L::TRANSFER_DST_OPTIMAL, L::PRESENT_SRC_KHR);
            d.end_command_buffer(cmd)?;
            let (wait, signal, cmds) = ([self.acquired], [self.done[i]], [cmd]);
            let submit = vk::SubmitInfo::default()
                .wait_semaphores(&wait)
                .wait_dst_stage_mask(&[S::ALL_COMMANDS])
                .command_buffers(&cmds)
                .signal_semaphores(&signal);
            d.queue_submit(self.queue, &[submit], self.fence)?;
            let (sc, idx) = ([self.sc], [i as u32]);
            let info = vk::PresentInfoKHR::default()
                .wait_semaphores(&signal)
                .swapchains(&sc)
                .image_indices(&idx);
            self.swap.queue_present(self.queue, &info)?;
            self.seen[i] = true;
            if !old || !read {
                return Ok(None);
            }
            d.wait_for_fences(&[self.fence], true, u64::MAX)?;
            let bytes = std::slice::from_raw_parts(self.map, (W * H * 4) as usize);
            Ok(Some(
                bytes
                    .chunks_exact(4)
                    .map(|p| [p[0], p[1], p[2], p[3]])
                    .collect(),
            ))
        }
    }

    /// Bilder zeigen und den letzten Stand liefern.
    unsafe fn run(&mut self, n: u32) -> R<Vec<[u8; 4]>> {
        let mut last = None;
        for i in 0..n {
            if let Some(p) = unsafe { self.frame(i + 8 > n)? } {
                last = Some(p);
            }
        }
        last.ok_or_else(|| "kein Bild".into())
    }
}

impl Drop for Gpu {
    fn drop(&mut self) {
        unsafe {
            let d = &self.d;
            let _ = d.device_wait_idle();
            for s in self.done.drain(..) {
                d.destroy_semaphore(s, None);
            }
            self.swap.destroy_swapchain(self.sc, None);
            d.destroy_semaphore(self.acquired, None);
            d.destroy_fence(self.fence, None);
            d.destroy_command_pool(self.pool, None);
            d.destroy_buffer(self.buffer, None);
            d.free_memory(self.memory, None);
            d.destroy_device(None);
            self.surfaces.destroy_surface(self.surface, None);
            self.instance.destroy_instance(None);
        }
    }
}

const BLUE: [u8; 4] = [255, 0, 0, 255];
const RED: [u8; 4] = [0, 0, 255, 255];
const GREEN: [u8; 4] = [0, 255, 0, 255];

fn at(p: &[[u8; 4]], x: u32, y: u32) -> [u8; 4] {
    p[(y * W + x) as usize]
}

fn expect(what: &str, got: [u8; 4], want: [u8; 4]) -> R<()> {
    if (0..3).any(|k| got[k].abs_diff(want[k]) > 2) {
        return Err(format!("{what}: {got:?} statt {want:?}").into());
    }
    println!("  {what}: stimmt");
    Ok(())
}

fn check(window: &Window, server: &TcpListener, token: &str) -> R<()> {
    unsafe {
        let mut g = Gpu::new(window)?;
        g.rebuild()?;
        let (mut out, _) = server.accept()?;
        let mut lines = BufReader::new(out.try_clone()?);
        let mut line = String::new();
        lines.read_line(&mut line)?;
        if !line.contains(token) {
            return Err(format!("falsche Anmeldung: {line}").into());
        }
        let p = g.run(8)?;
        expect("ohne Overlay", at(&p, 1100, 650), BLUE)?;

        packet(&mut out, 1, &image([255, 0, 0, 255]))?;
        packet(&mut out, 2, &encode(&quad(30.0)))?;
        std::thread::sleep(Duration::from_millis(100));
        let p = g.run(20)?;
        // 1080 -> 720: Faktor 2/3; Rechteck 1060..1260 x 640..700
        expect("mitten im Rechteck", at(&p, 1160, 670), RED)?;
        expect("linke obere Ecke", at(&p, 1061, 641), RED)?;
        expect("rechte untere Ecke", at(&p, 1258, 698), RED)?;
        expect("links daneben", at(&p, 1057, 670), BLUE)?;
        expect("darueber", at(&p, 1160, 637), BLUE)?;

        packet(&mut out, 1, &image([0, 255, 0, 255]))?;
        std::thread::sleep(Duration::from_millis(100));
        let p = g.run(20)?;
        expect("Bild ersetzt", at(&p, 1160, 670), GREEN)?;

        g.rebuild()?;
        let p = g.run(20)?;
        expect("nach Swapchain-Neubau", at(&p, 1160, 670), GREEN)?;

        let start = Instant::now();
        g.run(2000)?;
        println!(
            "  2000 Bilder mit Overlay: {:.2} s",
            start.elapsed().as_secs_f32()
        );
        packet(&mut out, 4, &[])?;
        std::thread::sleep(Duration::from_millis(100));
        let start = Instant::now();
        let p = g.run(2000)?;
        println!(
            "  2000 Bilder ohne Overlay: {:.2} s",
            start.elapsed().as_secs_f32()
        );
        expect("geleert", at(&p, 1160, 670), BLUE)?;
        line.clear();
        lines.read_line(&mut line)?;
        println!("  Meldung: {}", line.trim());

        packet(&mut out, 1, &image([255, 0, 0, 255]))?;
        packet(&mut out, 2, &encode(&quad(30.0)))?;
        std::thread::sleep(Duration::from_millis(100));
        g.run(10)?;
        out.shutdown(std::net::Shutdown::Both)?;
        std::thread::sleep(Duration::from_millis(200));
        let p = g.run(20)?;
        expect("Launcher weg", at(&p, 1160, 670), BLUE)?;
    }
    Ok(())
}

struct App {
    server: TcpListener,
    done: bool,
}

impl ApplicationHandler for App {
    fn resumed(&mut self, el: &ActiveEventLoop) {
        if std::mem::replace(&mut self.done, true) {
            return;
        }
        let attrs = Window::default_attributes()
            .with_visible(false)
            .with_decorations(false)
            .with_inner_size(winit::dpi::PhysicalSize::new(W, H));
        let window = el.create_window(attrs).expect("Fenster");
        match check(&window, &self.server, "abc123") {
            Ok(()) => println!("Alles in Ordnung."),
            Err(e) => {
                eprintln!("FEHLER: {e}");
                std::process::exit(1);
            }
        }
        el.exit();
    }

    fn window_event(&mut self, _: &ActiveEventLoop, _: WindowId, _: WindowEvent) {}
}

fn main() {
    let server = TcpListener::bind("127.0.0.1:0").expect("Anschluss");
    let addr = server.local_addr().expect("Adresse").to_string();
    // Beispiele liegen in target/<profil>/examples, die Schicht eine Ebene darueber
    let exe = std::env::current_exe().expect("exe");
    let dir = exe.parent().and_then(|p| p.parent()).expect("Ordner");
    let library = if cfg!(windows) {
        "mett_overlay.dll"
    } else {
        "libmett_overlay.so"
    };
    let path = dir
        .join(library)
        .display()
        .to_string()
        .replace('\\', "\\\\");
    let manifest = include_str!("../layer.json").replace("@LIBRARY@", &path);
    let folder = dir.join("probe-layer");
    std::fs::create_dir_all(&folder).expect("Ordner");
    std::fs::write(folder.join("mett_overlay.json"), manifest).expect("Beschreibung");
    // SAFETY: noch laeuft kein zweiter Faden
    unsafe {
        std::env::set_var("VK_ADD_LAYER_PATH", &folder);
        std::env::set_var("VK_INSTANCE_LAYERS", "VK_LAYER_METT_overlay");
        std::env::set_var("METT_OVERLAY_ADDR", &addr);
        std::env::set_var("METT_OVERLAY_TOKEN", "abc123");
    }
    let mut app = App {
        server,
        done: false,
    };
    let el = EventLoop::new().expect("Ereignisschleife");
    el.run_app(&mut app).expect("Ablauf");
}
