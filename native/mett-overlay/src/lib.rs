//! Overlay des Mett Launchers als Vulkan-Schicht. Der Vulkan-Lader schaltet sie zwischen Spiel und
//! Treiber, wenn der Launcher das Spiel gestartet hat. Geht etwas schief, reicht sie alles nur durch.

pub mod draw;
pub mod link;
pub mod scene;

use ash::vk::{self, Handle};
use draw::{Desc, Painter, SetLoaderData};
use std::collections::HashMap;
use std::ffi::{CStr, c_char, c_void};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, LazyLock, Mutex, RwLock};
use std::time::Instant;

/// Ein Fehler schaltet die Schicht fuer diese Sitzung ab.
static BROKEN: AtomicBool = AtomicBool::new(false);

const LOADER_INSTANCE: i32 = 47;
const LOADER_DEVICE: i32 = 48;
const LINK_INFO: i32 = 0;
const DATA_CALLBACK: i32 = 1;

type Gipa = vk::PFN_vkGetInstanceProcAddr;
type Gdpa = vk::PFN_vkGetDeviceProcAddr;

#[repr(C)]
struct InstanceLink {
    next: *mut InstanceLink,
    gipa: Gipa,
    physical: *const c_void,
}

#[repr(C)]
struct DeviceLink {
    next: *mut DeviceLink,
    gipa: Gipa,
    gdpa: Gdpa,
}

/// Kopf von `VkLayerInstanceCreateInfo` und `VkLayerDeviceCreateInfo`.
#[repr(C)]
struct Link<T> {
    s_type: i32,
    p_next: *const c_void,
    function: i32,
    /// Verkettung oder Funktion, je nach `function`
    value: *mut T,
}

struct Instance {
    raw: vk::Instance,
    gipa: Gipa,
    fns: ash::Instance,
}

struct SwapchainInfo {
    format: vk::Format,
    space: vk::ColorSpaceKHR,
    extent: vk::Extent2D,
    /// Zeichnen erlaubt (Nutzung als Farbziel)
    usable: bool,
    /// entsteht, wenn zum ersten Mal etwas zu zeigen ist
    painter: Option<Painter>,
}

struct Device {
    raw: vk::Device,
    gdpa: Gdpa,
    fns: ash::Device,
    swapchain: ash::khr::swapchain::DeviceFn,
    memory: vk::PhysicalDeviceMemoryProperties,
    set_loader_data: Option<SetLoaderData>,
    /// Queue-Familien, die zeichnen koennen
    graphics: Vec<bool>,
    queues: RwLock<HashMap<u64, u32>>,
    swapchains: Mutex<HashMap<u64, SwapchainInfo>>,
}

static INSTANCES: LazyLock<RwLock<HashMap<usize, Arc<Instance>>>> = LazyLock::new(Default::default);
static DEVICES: LazyLock<RwLock<HashMap<usize, Arc<Device>>>> = LazyLock::new(Default::default);

/// Schluessel eines weiterreichbaren Objekts: sein erster Zeiger (gleich fuer alle Objekte eines
/// Geraets bzw. einer Instanz).
unsafe fn key(raw: u64) -> usize {
    unsafe { *(raw as *const usize) }
}

fn instance(key: usize) -> Option<Arc<Instance>> {
    INSTANCES.read().ok()?.get(&key).cloned()
}

fn device(key: usize) -> Option<Arc<Device>> {
    DEVICES.read().ok()?.get(&key).cloned()
}

/// Fuehrt `f` aus; ein Absturz darin schaltet die Schicht ab.
fn guarded<T>(f: impl FnOnce() -> T) -> Option<T> {
    if BROKEN.load(Ordering::Relaxed) {
        return None;
    }
    let r = catch_unwind(AssertUnwindSafe(f)).ok();
    if r.is_none() {
        BROKEN.store(true, Ordering::Relaxed);
    }
    r
}

/// Glied der Kette mit der gesuchten Aufgabe.
unsafe fn find_link<T>(start: *const c_void, s_type: i32, function: i32) -> Option<*mut Link<T>> {
    let mut p = start as *mut Link<T>;
    while !p.is_null() {
        unsafe {
            if (*p).s_type == s_type && (*p).function == function {
                return Some(p);
            }
            p = (*p).p_next as *mut Link<T>;
        }
    }
    None
}

unsafe extern "system" fn create_instance(
    info: *const vk::InstanceCreateInfo<'_>,
    alloc: *const vk::AllocationCallbacks<'_>,
    out: *mut vk::Instance,
) -> vk::Result {
    unsafe {
        let Some(link) = find_link::<InstanceLink>((*info).p_next, LOADER_INSTANCE, LINK_INFO)
        else {
            return vk::Result::ERROR_INITIALIZATION_FAILED;
        };
        let gipa = (*(*link).value).gipa;
        (*link).value = (*(*link).value).next;
        let Some(next) = gipa(vk::Instance::null(), c"vkCreateInstance".as_ptr()) else {
            return vk::Result::ERROR_INITIALIZATION_FAILED;
        };
        let next: vk::PFN_vkCreateInstance = std::mem::transmute(next);
        let r = next(info, alloc, out);
        if r == vk::Result::SUCCESS {
            guarded(|| {
                let raw = *out;
                let fns = ash::Instance::load_with(
                    |n| {
                        std::mem::transmute::<vk::PFN_vkVoidFunction, *const c_void>(gipa(
                            raw,
                            n.as_ptr(),
                        ))
                    },
                    raw,
                );
                let i = Arc::new(Instance { raw, gipa, fns });
                if let Ok(mut m) = INSTANCES.write() {
                    m.insert(key(raw.as_raw()), i);
                }
            });
        }
        r
    }
}

unsafe extern "system" fn destroy_instance(
    raw: vk::Instance,
    alloc: *const vk::AllocationCallbacks<'_>,
) {
    if raw == vk::Instance::null() {
        return;
    }
    unsafe {
        let k = key(raw.as_raw());
        let Some(i) = instance(k) else {
            return;
        };
        if let Ok(mut m) = INSTANCES.write() {
            m.remove(&k);
        }
        (i.fns.fp_v1_0().destroy_instance)(raw, alloc);
    }
}

unsafe extern "system" fn create_device(
    physical: vk::PhysicalDevice,
    info: *const vk::DeviceCreateInfo<'_>,
    alloc: *const vk::AllocationCallbacks<'_>,
    out: *mut vk::Device,
) -> vk::Result {
    unsafe {
        let Some(link) = find_link::<DeviceLink>((*info).p_next, LOADER_DEVICE, LINK_INFO) else {
            return vk::Result::ERROR_INITIALIZATION_FAILED;
        };
        let (gipa, gdpa) = ((*(*link).value).gipa, (*(*link).value).gdpa);
        (*link).value = (*(*link).value).next;
        let inst = instance(key(physical.as_raw()));
        let raw_instance = inst.as_ref().map_or(vk::Instance::null(), |i| i.raw);
        let Some(next) = gipa(raw_instance, c"vkCreateDevice".as_ptr()) else {
            return vk::Result::ERROR_INITIALIZATION_FAILED;
        };
        let next: vk::PFN_vkCreateDevice = std::mem::transmute(next);
        let callback =
            find_link::<c_void>((*info).p_next, LOADER_DEVICE, DATA_CALLBACK).map(|l| (*l).value);
        let r = next(physical, info, alloc, out);
        if r == vk::Result::SUCCESS {
            guarded(|| {
                let Some(inst) = inst else {
                    return;
                };
                let raw = *out;
                let load = |n: &CStr| {
                    std::mem::transmute::<vk::PFN_vkVoidFunction, *const c_void>(gdpa(
                        raw,
                        n.as_ptr(),
                    ))
                };
                let families = inst
                    .fns
                    .get_physical_device_queue_family_properties(physical);
                let d = Device {
                    raw,
                    gdpa,
                    fns: ash::Device::load_with(load, raw),
                    swapchain: ash::khr::swapchain::DeviceFn::load(load),
                    memory: inst.fns.get_physical_device_memory_properties(physical),
                    set_loader_data: callback
                        .filter(|c| !c.is_null())
                        .map(|c| std::mem::transmute::<*mut c_void, SetLoaderData>(c)),
                    graphics: families
                        .iter()
                        .map(|f| f.queue_flags.contains(vk::QueueFlags::GRAPHICS))
                        .collect(),
                    queues: Default::default(),
                    swapchains: Default::default(),
                };
                if let Ok(mut m) = DEVICES.write() {
                    m.insert(key(raw.as_raw()), Arc::new(d));
                }
                link::shared(); // Verbindung schon jetzt aufbauen
            });
        }
        r
    }
}

unsafe extern "system" fn destroy_device(
    raw: vk::Device,
    alloc: *const vk::AllocationCallbacks<'_>,
) {
    if raw == vk::Device::null() {
        return;
    }
    unsafe {
        let k = key(raw.as_raw());
        let Some(d) = device(k) else {
            return;
        };
        guarded(|| {
            if let Ok(mut m) = d.swapchains.lock() {
                for (_, mut s) in m.drain() {
                    if let Some(mut p) = s.painter.take() {
                        p.destroy();
                    }
                }
            }
        });
        if let Ok(mut m) = DEVICES.write() {
            m.remove(&k);
        }
        (d.fns.fp_v1_0().destroy_device)(raw, alloc);
    }
}

fn remember_queue(d: &Device, queue: vk::Queue, family: u32) {
    if let Ok(mut m) = d.queues.write() {
        m.insert(queue.as_raw(), family);
    }
}

unsafe extern "system" fn get_device_queue(
    raw: vk::Device,
    family: u32,
    index: u32,
    out: *mut vk::Queue,
) {
    unsafe {
        if let Some(d) = device(key(raw.as_raw())) {
            (d.fns.fp_v1_0().get_device_queue)(raw, family, index, out);
            guarded(|| remember_queue(&d, *out, family));
        }
    }
}

unsafe extern "system" fn get_device_queue2(
    raw: vk::Device,
    info: *const vk::DeviceQueueInfo2<'_>,
    out: *mut vk::Queue,
) {
    unsafe {
        if let Some(d) = device(key(raw.as_raw())) {
            (d.fns.fp_v1_1().get_device_queue2)(raw, info, out);
            guarded(|| remember_queue(&d, *out, (*info).queue_family_index));
        }
    }
}

unsafe extern "system" fn create_swapchain(
    raw: vk::Device,
    info: *const vk::SwapchainCreateInfoKHR<'_>,
    alloc: *const vk::AllocationCallbacks<'_>,
    out: *mut vk::SwapchainKHR,
) -> vk::Result {
    unsafe {
        let Some(d) = device(key(raw.as_raw())) else {
            return vk::Result::ERROR_INITIALIZATION_FAILED;
        };
        let mut mine = *info;
        // Farbziel brauchen wir; die Spiele haben es ohnehin
        mine.image_usage |= vk::ImageUsageFlags::COLOR_ATTACHMENT;
        let mut r = (d.swapchain.create_swapchain_khr)(raw, &mine, alloc, out);
        if r != vk::Result::SUCCESS {
            r = (d.swapchain.create_swapchain_khr)(raw, info, alloc, out);
        }
        if r == vk::Result::SUCCESS {
            guarded(|| {
                let s = SwapchainInfo {
                    format: mine.image_format,
                    space: mine.image_color_space,
                    extent: mine.image_extent,
                    usable: mine.image_array_layers == 1,
                    painter: None,
                };
                if let Ok(mut m) = d.swapchains.lock() {
                    m.insert((*out).as_raw(), s);
                }
            });
        }
        r
    }
}

unsafe extern "system" fn destroy_swapchain(
    raw: vk::Device,
    sc: vk::SwapchainKHR,
    alloc: *const vk::AllocationCallbacks<'_>,
) {
    unsafe {
        let Some(d) = device(key(raw.as_raw())) else {
            return;
        };
        guarded(|| {
            let info = d
                .swapchains
                .lock()
                .ok()
                .and_then(|mut m| m.remove(&sc.as_raw()));
            if let Some(mut p) = info.and_then(|s| s.painter) {
                p.destroy();
            }
        });
        (d.swapchain.destroy_swapchain_khr)(raw, sc, alloc);
    }
}

unsafe extern "system" fn queue_present(
    queue: vk::Queue,
    info: *const vk::PresentInfoKHR<'_>,
) -> vk::Result {
    unsafe {
        let Some(d) = device(key(queue.as_raw())) else {
            return vk::Result::ERROR_DEVICE_LOST;
        };
        let semaphore = guarded(|| paint(&d, queue, &*info)).flatten();
        let Some(semaphore) = semaphore else {
            return (d.swapchain.queue_present_khr)(queue, info);
        };
        let wait = [semaphore];
        let mut mine = *info;
        mine.wait_semaphore_count = 1;
        mine.p_wait_semaphores = wait.as_ptr();
        (d.swapchain.queue_present_khr)(queue, &mine)
    }
}

/// Overlay zeichnen; liefert den Semaphor, auf den das Anzeigen warten muss.
unsafe fn paint(
    d: &Device,
    queue: vk::Queue,
    info: &vk::PresentInfoKHR<'_>,
) -> Option<vk::Semaphore> {
    let shared = link::shared()?;
    shared.frames.fetch_add(1, Ordering::Relaxed);
    // mehrere Fenster auf einmal: nicht unser Fall
    if info.swapchain_count != 1 {
        return None;
    }
    unsafe {
        let (sc, index) = (*info.p_swapchains, *info.p_image_indices);
        let mut all = d.swapchains.lock().ok()?;
        let s = all.get_mut(&sc.as_raw()).filter(|s| s.usable)?;
        let size = (s.extent.width, s.extent.height);
        shared.width.store(size.0, Ordering::Relaxed);
        shared.height.store(size.1, Ordering::Relaxed);
        let mut scene = shared.scene.lock().ok()?;
        if scene.is_empty() {
            return None;
        }
        let list = scene.place(size, Instant::now());
        if list.is_empty() {
            return None;
        }
        let family = *d.queues.read().ok()?.get(&queue.as_raw())?;
        if !d.graphics.get(family as usize).copied().unwrap_or(false) {
            return None;
        }
        if s.painter.is_none() {
            let images = swapchain_images(d, sc)?;
            let desc = Desc {
                images: &images,
                format: s.format,
                space: s.space,
                extent: s.extent,
                family,
            };
            match Painter::new(&d.fns, d.memory, &desc, d.set_loader_data?) {
                Ok(p) => s.painter = Some(p),
                Err(_) => {
                    s.usable = false;
                    return None;
                }
            }
        }
        let wait = match info.wait_semaphore_count {
            0 => &[],
            n => std::slice::from_raw_parts(info.p_wait_semaphores, n as usize),
        };
        let p = s.painter.as_mut()?;
        match p.paint(queue, index as usize, wait, &list, &scene.images) {
            Ok(done) => Some(done),
            Err(_) => {
                s.usable = false;
                None
            }
        }
    }
}

unsafe fn swapchain_images(d: &Device, sc: vk::SwapchainKHR) -> Option<Vec<vk::Image>> {
    unsafe {
        let get = d.swapchain.get_swapchain_images_khr;
        let mut n = 0;
        get(d.raw, sc, &mut n, std::ptr::null_mut()).result().ok()?;
        let mut images = vec![vk::Image::null(); n as usize];
        get(d.raw, sc, &mut n, images.as_mut_ptr()).result().ok()?;
        Some(images)
    }
}

macro_rules! void {
    ($f:expr) => {
        Some(std::mem::transmute::<*const (), unsafe extern "system" fn()>($f as *const ()))
    };
}

/// Eigene Funktion zu einem Namen.
fn own(name: &CStr, device_only: bool) -> vk::PFN_vkVoidFunction {
    unsafe {
        let device = match name.to_bytes() {
            b"vkGetDeviceProcAddr" => void!(get_device_proc_addr),
            b"vkDestroyDevice" => void!(destroy_device),
            b"vkGetDeviceQueue" => void!(get_device_queue),
            b"vkGetDeviceQueue2" => void!(get_device_queue2),
            b"vkCreateSwapchainKHR" => void!(create_swapchain),
            b"vkDestroySwapchainKHR" => void!(destroy_swapchain),
            b"vkQueuePresentKHR" => void!(queue_present),
            _ => None,
        };
        if device.is_some() || device_only {
            return device;
        }
        match name.to_bytes() {
            b"vkGetInstanceProcAddr" => void!(get_instance_proc_addr),
            b"vkCreateInstance" => void!(create_instance),
            b"vkDestroyInstance" => void!(destroy_instance),
            b"vkCreateDevice" => void!(create_device),
            _ => None,
        }
    }
}

unsafe extern "system" fn get_instance_proc_addr(
    raw: vk::Instance,
    name: *const c_char,
) -> vk::PFN_vkVoidFunction {
    unsafe {
        let n = CStr::from_ptr(name);
        if let Some(f) = own(n, false) {
            return Some(f);
        }
        if raw == vk::Instance::null() {
            return None;
        }
        let i = instance(key(raw.as_raw()))?;
        (i.gipa)(raw, name)
    }
}

unsafe extern "system" fn get_device_proc_addr(
    raw: vk::Device,
    name: *const c_char,
) -> vk::PFN_vkVoidFunction {
    unsafe {
        let d = device(key(raw.as_raw()))?;
        // nur anbieten, was es darunter auch gibt
        let below = (d.gdpa)(raw, name)?;
        own(CStr::from_ptr(name), true).or(Some(below))
    }
}

/// `VkNegotiateLayerInterface`
#[repr(C)]
pub struct Negotiate {
    s_type: i32,
    p_next: *mut c_void,
    version: u32,
    gipa: Option<Gipa>,
    gdpa: Option<Gdpa>,
    physical: *const c_void,
}

/// Einstieg fuer den Vulkan-Lader.
///
/// # Safety
/// `n` kommt vom Lader.
#[unsafe(no_mangle)]
#[allow(non_snake_case)]
pub unsafe extern "system" fn vkNegotiateLoaderLayerInterfaceVersion(
    n: *mut Negotiate,
) -> vk::Result {
    unsafe {
        if n.is_null() || (*n).version < 2 {
            return vk::Result::ERROR_INITIALIZATION_FAILED;
        }
        (*n).version = 2;
        (*n).gipa = Some(get_instance_proc_addr);
        (*n).gdpa = Some(get_device_proc_addr);
        (*n).physical = std::ptr::null();
    }
    vk::Result::SUCCESS
}
