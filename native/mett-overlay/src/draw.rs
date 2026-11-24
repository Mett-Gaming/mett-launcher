//! Zeichnen auf eine Swapchain: ein Durchgang je Bild, nur wenn etwas zu zeigen ist.

use crate::scene::{Image, Placed};
use ash::vk;
use std::collections::HashMap;
use std::ffi::c_void;
use std::sync::Arc;

pub type SetLoaderData = unsafe extern "system" fn(vk::Device, *mut c_void) -> vk::Result;

/// Verschiedene Bilder, die gleichzeitig auf der Karte liegen
const MAX_TEXTURES: u32 = 128;
/// Weiss des Overlays in HDR (Nits)
const PAPER_WHITE: f32 = 200.0;

macro_rules! spv {
    ($n:literal) => {
        include_bytes!(concat!(env!("OUT_DIR"), "/", $n, ".spv"))
    };
}

/// Ein Bild auf der Karte.
struct Texture {
    image: vk::Image,
    memory: vk::DeviceMemory,
    view: vk::ImageView,
    set: vk::DescriptorSet,
    version: u64,
}

/// Swapchain-Bild.
struct Target {
    view: vk::ImageView,
    frame: vk::Framebuffer,
    cmd: vk::CommandBuffer,
    fence: vk::Fence,
    done: vk::Semaphore,
    /// Zwischenpuffer, frei sobald der Fence meldet
    staging: Vec<(vk::Buffer, vk::DeviceMemory)>,
    /// ersetzte Bilder
    old: Vec<Texture>,
}

/// Was zum Zeichnen auf eine Swapchain gehoert.
pub struct Painter {
    d: ash::Device,
    memory: vk::PhysicalDeviceMemoryProperties,
    extent: vk::Extent2D,
    mode: f32,
    pass: vk::RenderPass,
    set_layout: vk::DescriptorSetLayout,
    layout: vk::PipelineLayout,
    pipe: vk::Pipeline,
    sampler: vk::Sampler,
    sets: vk::DescriptorPool,
    pool: vk::CommandPool,
    targets: Vec<Target>,
    textures: HashMap<u32, Texture>,
}

/// Angaben zu einer Swapchain.
pub struct Desc<'a> {
    pub images: &'a [vk::Image],
    pub format: vk::Format,
    pub space: vk::ColorSpaceKHR,
    pub extent: vk::Extent2D,
    pub family: u32,
}

fn mode(format: vk::Format, space: vk::ColorSpaceKHR) -> f32 {
    use vk::Format as F;
    match space {
        vk::ColorSpaceKHR::HDR10_ST2084_EXT => 2.0,
        vk::ColorSpaceKHR::EXTENDED_SRGB_LINEAR_EXT => 3.0,
        _ if matches!(
            format,
            F::B8G8R8A8_SRGB | F::R8G8B8A8_SRGB | F::A8B8G8R8_SRGB_PACK32
        ) =>
        {
            1.0
        }
        _ => 0.0,
    }
}

impl Painter {
    /// # Safety
    /// Alles in `desc` gehoert zu `d`, `set_loader_data` zum Lader dieses Geraets.
    pub unsafe fn new(
        d: &ash::Device,
        memory: vk::PhysicalDeviceMemoryProperties,
        desc: &Desc,
        set_loader_data: SetLoaderData,
    ) -> Result<Self, vk::Result> {
        unsafe {
            let mut p = Self {
                d: d.clone(),
                memory,
                extent: desc.extent,
                mode: mode(desc.format, desc.space),
                pass: vk::RenderPass::null(),
                set_layout: vk::DescriptorSetLayout::null(),
                layout: vk::PipelineLayout::null(),
                pipe: vk::Pipeline::null(),
                sampler: vk::Sampler::null(),
                sets: vk::DescriptorPool::null(),
                pool: vk::CommandPool::null(),
                targets: Vec::new(),
                textures: HashMap::new(),
            };
            match p.build(desc, set_loader_data) {
                Ok(()) => Ok(p),
                Err(e) => {
                    p.destroy();
                    Err(e)
                }
            }
        }
    }

    unsafe fn build(&mut self, desc: &Desc, set: SetLoaderData) -> Result<(), vk::Result> {
        unsafe {
            let d = self.d.clone();
            self.pass = render_pass(&d, desc.format)?;
            let clamp = vk::SamplerAddressMode::CLAMP_TO_EDGE;
            let info = vk::SamplerCreateInfo::default()
                .mag_filter(vk::Filter::LINEAR)
                .min_filter(vk::Filter::LINEAR)
                .address_mode_u(clamp)
                .address_mode_v(clamp);
            self.sampler = d.create_sampler(&info, None)?;
            let binding = |b, ty| {
                vk::DescriptorSetLayoutBinding::default()
                    .binding(b)
                    .descriptor_type(ty)
                    .descriptor_count(1)
                    .stage_flags(vk::ShaderStageFlags::FRAGMENT)
            };
            let bindings = [
                binding(0, vk::DescriptorType::SAMPLED_IMAGE),
                binding(1, vk::DescriptorType::SAMPLER),
            ];
            let info = vk::DescriptorSetLayoutCreateInfo::default().bindings(&bindings);
            self.set_layout = d.create_descriptor_set_layout(&info, None)?;
            let sizes = [
                vk::DescriptorType::SAMPLED_IMAGE,
                vk::DescriptorType::SAMPLER,
            ]
            .map(|ty| vk::DescriptorPoolSize {
                ty,
                descriptor_count: MAX_TEXTURES,
            });
            let info = vk::DescriptorPoolCreateInfo::default()
                .flags(vk::DescriptorPoolCreateFlags::FREE_DESCRIPTOR_SET)
                .max_sets(MAX_TEXTURES)
                .pool_sizes(&sizes);
            self.sets = d.create_descriptor_pool(&info, None)?;
            let layouts = [self.set_layout];
            let push = [vk::PushConstantRange::default()
                .stage_flags(vk::ShaderStageFlags::VERTEX | vk::ShaderStageFlags::FRAGMENT)
                .size(32)];
            let info = vk::PipelineLayoutCreateInfo::default()
                .set_layouts(&layouts)
                .push_constant_ranges(&push);
            self.layout = d.create_pipeline_layout(&info, None)?;
            self.pipe = pipeline(&d, self.layout, self.pass)?;
            let info = vk::CommandPoolCreateInfo::default()
                .flags(vk::CommandPoolCreateFlags::RESET_COMMAND_BUFFER)
                .queue_family_index(desc.family);
            self.pool = d.create_command_pool(&info, None)?;
            let info = vk::CommandBufferAllocateInfo::default()
                .command_pool(self.pool)
                .level(vk::CommandBufferLevel::PRIMARY)
                .command_buffer_count(desc.images.len() as u32);
            let cmds = d.allocate_command_buffers(&info)?;
            for (&image, cmd) in desc.images.iter().zip(cmds) {
                // der Lader muss unsere Befehlspuffer kennen
                set(d.handle(), vk::Handle::as_raw(cmd) as *mut c_void).result()?;
                let info = vk::ImageViewCreateInfo::default()
                    .image(image)
                    .view_type(vk::ImageViewType::TYPE_2D)
                    .format(desc.format)
                    .subresource_range(color_range());
                let view = d.create_image_view(&info, None)?;
                let views = [view];
                let info = vk::FramebufferCreateInfo::default()
                    .render_pass(self.pass)
                    .attachments(&views)
                    .width(desc.extent.width)
                    .height(desc.extent.height)
                    .layers(1);
                let signaled = vk::FenceCreateInfo::default().flags(vk::FenceCreateFlags::SIGNALED);
                self.targets.push(Target {
                    view,
                    frame: d.create_framebuffer(&info, None)?,
                    cmd,
                    fence: d.create_fence(&signaled, None)?,
                    done: d.create_semaphore(&vk::SemaphoreCreateInfo::default(), None)?,
                    staging: Vec::new(),
                    old: Vec::new(),
                });
            }
            Ok(())
        }
    }

    /// Nimmt das Overlay fuer ein Bild auf und schickt es ab; liefert den Semaphor, auf den das
    /// Anzeigen warten muss.
    ///
    /// # Safety
    /// Die Queue gehoert zur Familie aus `Desc` und wird gerade von niemandem sonst benutzt.
    pub unsafe fn paint(
        &mut self,
        queue: vk::Queue,
        index: usize,
        wait: &[vk::Semaphore],
        list: &[Placed],
        images: &HashMap<u32, Arc<Image>>,
    ) -> Result<vk::Semaphore, vk::Result> {
        unsafe {
            let d = self.d.clone();
            let (cmd, fence, done, frame) = {
                let t = self.targets.get(index).ok_or(vk::Result::ERROR_UNKNOWN)?;
                (t.cmd, t.fence, t.done, t.frame)
            };
            d.wait_for_fences(&[fence], true, 1_000_000_000)?;
            self.release(index);
            let info = vk::CommandBufferBeginInfo::default()
                .flags(vk::CommandBufferUsageFlags::ONE_TIME_SUBMIT);
            d.begin_command_buffer(cmd, &info)?;
            for p in list {
                if let Some(image) = images.get(&p.image) {
                    self.ensure(cmd, index, p.image, image)?;
                }
            }
            let area = vk::Rect2D {
                offset: vk::Offset2D::default(),
                extent: self.extent,
            };
            let info = vk::RenderPassBeginInfo::default()
                .render_pass(self.pass)
                .framebuffer(frame)
                .render_area(area);
            d.cmd_begin_render_pass(cmd, &info, vk::SubpassContents::INLINE);
            let viewport = vk::Viewport {
                x: 0.0,
                y: 0.0,
                width: self.extent.width as f32,
                height: self.extent.height as f32,
                min_depth: 0.0,
                max_depth: 1.0,
            };
            d.cmd_set_viewport(cmd, 0, &[viewport]);
            d.cmd_set_scissor(cmd, 0, &[area]);
            let gfx = vk::PipelineBindPoint::GRAPHICS;
            d.cmd_bind_pipeline(cmd, gfx, self.pipe);
            for p in list {
                let Some(t) = self.textures.get(&p.image) else {
                    continue;
                };
                d.cmd_bind_descriptor_sets(cmd, gfx, self.layout, 0, &[t.set], &[]);
                let mut push = [0u8; 32];
                let more = [p.alpha, self.mode, PAPER_WHITE, 0.0];
                let values = p.rect.iter().chain(&more);
                for (k, v) in values.enumerate() {
                    push[k * 4..k * 4 + 4].copy_from_slice(&v.to_le_bytes());
                }
                let stages = vk::ShaderStageFlags::VERTEX | vk::ShaderStageFlags::FRAGMENT;
                d.cmd_push_constants(cmd, self.layout, stages, 0, &push);
                d.cmd_draw(cmd, 4, 1, 0, 0);
            }
            d.cmd_end_render_pass(cmd);
            d.end_command_buffer(cmd)?;
            let stages = vec![vk::PipelineStageFlags::ALL_COMMANDS; wait.len()];
            let (cmds, signal) = ([cmd], [done]);
            let submit = vk::SubmitInfo::default()
                .wait_semaphores(wait)
                .wait_dst_stage_mask(&stages)
                .command_buffers(&cmds)
                .signal_semaphores(&signal);
            d.reset_fences(&[fence])?;
            d.queue_submit(queue, &[submit], fence)?;
            Ok(done)
        }
    }

    /// Bild auf die Karte bringen, falls neu oder geaendert.
    unsafe fn ensure(
        &mut self,
        cmd: vk::CommandBuffer,
        index: usize,
        id: u32,
        source: &Image,
    ) -> Result<(), vk::Result> {
        let fits = |t: &Texture| t.version == source.version;
        if self.textures.get(&id).is_some_and(fits) {
            return Ok(());
        }
        unsafe {
            let t = self.upload(cmd, index, source)?;
            if let Some(old) = self.textures.insert(id, t) {
                // andere Bilder im Flug koennen es noch benutzen: erst nach einer Runde frei
                self.targets[index].old.push(old);
            }
        }
        Ok(())
    }

    unsafe fn upload(
        &mut self,
        cmd: vk::CommandBuffer,
        index: usize,
        s: &Image,
    ) -> Result<Texture, vk::Result> {
        unsafe {
            let d = self.d.clone();
            let info = vk::BufferCreateInfo::default()
                .size(s.rgba.len() as u64)
                .usage(vk::BufferUsageFlags::TRANSFER_SRC);
            let buffer = d.create_buffer(&info, None)?;
            let host =
                vk::MemoryPropertyFlags::HOST_VISIBLE | vk::MemoryPropertyFlags::HOST_COHERENT;
            let req = d.get_buffer_memory_requirements(buffer);
            let memory = self.allocate(req, host)?;
            d.bind_buffer_memory(buffer, memory, 0)?;
            let p = d.map_memory(memory, 0, vk::WHOLE_SIZE, Default::default())?;
            std::ptr::copy_nonoverlapping(s.rgba.as_ptr(), p.cast(), s.rgba.len());
            d.unmap_memory(memory);
            self.targets[index].staging.push((buffer, memory));

            let format = vk::Format::R8G8B8A8_UNORM;
            let extent = vk::Extent3D {
                width: s.width,
                height: s.height,
                depth: 1,
            };
            let info = vk::ImageCreateInfo::default()
                .image_type(vk::ImageType::TYPE_2D)
                .format(format)
                .extent(extent)
                .mip_levels(1)
                .array_layers(1)
                .samples(vk::SampleCountFlags::TYPE_1)
                .usage(vk::ImageUsageFlags::SAMPLED | vk::ImageUsageFlags::TRANSFER_DST);
            let image = d.create_image(&info, None)?;
            let req = d.get_image_memory_requirements(image);
            let memory = self.allocate(req, vk::MemoryPropertyFlags::DEVICE_LOCAL)?;
            d.bind_image_memory(image, memory, 0)?;
            let info = vk::ImageViewCreateInfo::default()
                .image(image)
                .view_type(vk::ImageViewType::TYPE_2D)
                .format(format)
                .subresource_range(color_range());
            let view = d.create_image_view(&info, None)?;
            let layouts = [self.set_layout];
            let info = vk::DescriptorSetAllocateInfo::default()
                .descriptor_pool(self.sets)
                .set_layouts(&layouts);
            let set = d.allocate_descriptor_sets(&info)?[0];
            let img = [vk::DescriptorImageInfo::default()
                .image_view(view)
                .image_layout(vk::ImageLayout::SHADER_READ_ONLY_OPTIMAL)];
            let smp = [vk::DescriptorImageInfo::default().sampler(self.sampler)];
            let w = |b, ty| {
                vk::WriteDescriptorSet::default()
                    .dst_set(set)
                    .dst_binding(b)
                    .descriptor_type(ty)
            };
            let writes = [
                w(0, vk::DescriptorType::SAMPLED_IMAGE).image_info(&img),
                w(1, vk::DescriptorType::SAMPLER).image_info(&smp),
            ];
            d.update_descriptor_sets(&writes, &[]);
            use vk::{AccessFlags as A, ImageLayout as L, PipelineStageFlags as S};
            let change = |from: L, to: L, a: A, b: A| {
                [vk::ImageMemoryBarrier::default()
                    .image(image)
                    .old_layout(from)
                    .new_layout(to)
                    .src_access_mask(a)
                    .dst_access_mask(b)
                    .src_queue_family_index(vk::QUEUE_FAMILY_IGNORED)
                    .dst_queue_family_index(vk::QUEUE_FAMILY_IGNORED)
                    .subresource_range(color_range())]
            };
            let none = vk::DependencyFlags::empty();
            let b = change(
                L::UNDEFINED,
                L::TRANSFER_DST_OPTIMAL,
                A::empty(),
                A::TRANSFER_WRITE,
            );
            d.cmd_pipeline_barrier(cmd, S::TOP_OF_PIPE, S::TRANSFER, none, &[], &[], &b);
            let region = vk::BufferImageCopy::default()
                .image_subresource(vk::ImageSubresourceLayers {
                    aspect_mask: vk::ImageAspectFlags::COLOR,
                    mip_level: 0,
                    base_array_layer: 0,
                    layer_count: 1,
                })
                .image_extent(extent);
            let dst = L::TRANSFER_DST_OPTIMAL;
            d.cmd_copy_buffer_to_image(cmd, buffer, image, dst, &[region]);
            let b = change(
                L::TRANSFER_DST_OPTIMAL,
                L::SHADER_READ_ONLY_OPTIMAL,
                A::TRANSFER_WRITE,
                A::SHADER_READ,
            );
            d.cmd_pipeline_barrier(cmd, S::TRANSFER, S::FRAGMENT_SHADER, none, &[], &[], &b);
            Ok(Texture {
                image,
                memory,
                view,
                set,
                version: s.version,
            })
        }
    }

    unsafe fn allocate(
        &self,
        req: vk::MemoryRequirements,
        flags: vk::MemoryPropertyFlags,
    ) -> Result<vk::DeviceMemory, vk::Result> {
        let kind = (0..self.memory.memory_type_count).find(|&i| {
            req.memory_type_bits & (1 << i) != 0
                && self.memory.memory_types[i as usize]
                    .property_flags
                    .contains(flags)
        });
        let info = vk::MemoryAllocateInfo::default()
            .allocation_size(req.size)
            .memory_type_index(kind.ok_or(vk::Result::ERROR_OUT_OF_DEVICE_MEMORY)?);
        unsafe { self.d.allocate_memory(&info, None) }
    }

    /// Zwischenpuffer und ersetzte Bilder der letzten Runde freigeben.
    unsafe fn release(&mut self, index: usize) {
        let t = &mut self.targets[index];
        let staging = std::mem::take(&mut t.staging);
        let old = std::mem::take(&mut t.old);
        unsafe {
            if !old.is_empty() {
                // selten: ein Bild wurde ersetzt
                let fences: Vec<_> = self.targets.iter().map(|t| t.fence).collect();
                let _ = self.d.wait_for_fences(&fences, true, 1_000_000_000);
            }
            for (b, m) in staging {
                self.d.destroy_buffer(b, None);
                self.d.free_memory(m, None);
            }
            for t in old {
                self.drop_texture(&t);
            }
        }
    }

    unsafe fn drop_texture(&self, t: &Texture) {
        unsafe {
            let _ = self.d.free_descriptor_sets(self.sets, &[t.set]);
            self.d.destroy_image_view(t.view, None);
            self.d.destroy_image(t.image, None);
            self.d.free_memory(t.memory, None);
        }
    }

    /// # Safety
    /// Das Geraet lebt noch; die Swapchain wird nicht mehr benutzt.
    pub unsafe fn destroy(&mut self) {
        unsafe {
            let fences: Vec<_> = self.targets.iter().map(|t| t.fence).collect();
            if !fences.is_empty() {
                let _ = self.d.wait_for_fences(&fences, true, 1_000_000_000);
            }
            for i in 0..self.targets.len() {
                self.release(i);
            }
            for t in std::mem::take(&mut self.textures).into_values() {
                self.drop_texture(&t);
            }
            for t in std::mem::take(&mut self.targets) {
                self.d.destroy_framebuffer(t.frame, None);
                self.d.destroy_image_view(t.view, None);
                self.d.destroy_fence(t.fence, None);
                self.d.destroy_semaphore(t.done, None);
            }
            // ungueltige (null) Handles sind erlaubt
            self.d.destroy_command_pool(self.pool, None);
            self.d.destroy_pipeline(self.pipe, None);
            self.d.destroy_pipeline_layout(self.layout, None);
            self.d.destroy_descriptor_pool(self.sets, None);
            self.d.destroy_descriptor_set_layout(self.set_layout, None);
            self.d.destroy_sampler(self.sampler, None);
            self.d.destroy_render_pass(self.pass, None);
        }
    }
}

fn color_range() -> vk::ImageSubresourceRange {
    vk::ImageSubresourceRange {
        aspect_mask: vk::ImageAspectFlags::COLOR,
        base_mip_level: 0,
        level_count: 1,
        base_array_layer: 0,
        layer_count: 1,
    }
}

unsafe fn render_pass(d: &ash::Device, format: vk::Format) -> Result<vk::RenderPass, vk::Result> {
    let present = vk::ImageLayout::PRESENT_SRC_KHR;
    let att = [vk::AttachmentDescription::default()
        .format(format)
        .samples(vk::SampleCountFlags::TYPE_1)
        .load_op(vk::AttachmentLoadOp::LOAD)
        .store_op(vk::AttachmentStoreOp::STORE)
        .initial_layout(present)
        .final_layout(present)];
    let refs = [vk::AttachmentReference {
        attachment: 0,
        layout: vk::ImageLayout::COLOR_ATTACHMENT_OPTIMAL,
    }];
    let sub = [vk::SubpassDescription::default()
        .pipeline_bind_point(vk::PipelineBindPoint::GRAPHICS)
        .color_attachments(&refs)];
    let out = vk::PipelineStageFlags::COLOR_ATTACHMENT_OUTPUT;
    let deps = [vk::SubpassDependency::default()
        .src_subpass(vk::SUBPASS_EXTERNAL)
        .src_stage_mask(vk::PipelineStageFlags::ALL_COMMANDS)
        .dst_stage_mask(out)
        .dst_access_mask(
            vk::AccessFlags::COLOR_ATTACHMENT_READ | vk::AccessFlags::COLOR_ATTACHMENT_WRITE,
        )];
    let info = vk::RenderPassCreateInfo::default()
        .attachments(&att)
        .subpasses(&sub)
        .dependencies(&deps);
    unsafe { d.create_render_pass(&info, None) }
}

unsafe fn pipeline(
    d: &ash::Device,
    layout: vk::PipelineLayout,
    pass: vk::RenderPass,
) -> Result<vk::Pipeline, vk::Result> {
    unsafe {
        let module = |bytes: &[u8]| {
            let code: Vec<u32> = bytes
                .chunks_exact(4)
                .map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]]))
                .collect();
            d.create_shader_module(&vk::ShaderModuleCreateInfo::default().code(&code), None)
        };
        let (vs, fs) = (module(spv!("vs_main"))?, module(spv!("fs_main"))?);
        let stage = |s, m, name| {
            vk::PipelineShaderStageCreateInfo::default()
                .stage(s)
                .module(m)
                .name(name)
        };
        let stages = [
            stage(vk::ShaderStageFlags::VERTEX, vs, c"vs_main"),
            stage(vk::ShaderStageFlags::FRAGMENT, fs, c"fs_main"),
        ];
        let vertex = vk::PipelineVertexInputStateCreateInfo::default();
        let assembly = vk::PipelineInputAssemblyStateCreateInfo::default()
            .topology(vk::PrimitiveTopology::TRIANGLE_STRIP);
        let viewport = vk::PipelineViewportStateCreateInfo::default()
            .viewport_count(1)
            .scissor_count(1);
        let raster = vk::PipelineRasterizationStateCreateInfo::default().line_width(1.0);
        let multi = vk::PipelineMultisampleStateCreateInfo::default()
            .rasterization_samples(vk::SampleCountFlags::TYPE_1);
        use vk::BlendFactor as F;
        let att = [vk::PipelineColorBlendAttachmentState::default()
            .blend_enable(true)
            .src_color_blend_factor(F::ONE)
            .dst_color_blend_factor(F::ONE_MINUS_SRC_ALPHA)
            .src_alpha_blend_factor(F::ONE)
            .dst_alpha_blend_factor(F::ONE_MINUS_SRC_ALPHA)
            .color_write_mask(vk::ColorComponentFlags::RGBA)];
        let blend = vk::PipelineColorBlendStateCreateInfo::default().attachments(&att);
        let states = [vk::DynamicState::VIEWPORT, vk::DynamicState::SCISSOR];
        let dynamic = vk::PipelineDynamicStateCreateInfo::default().dynamic_states(&states);
        let info = vk::GraphicsPipelineCreateInfo::default()
            .stages(&stages)
            .vertex_input_state(&vertex)
            .input_assembly_state(&assembly)
            .viewport_state(&viewport)
            .rasterization_state(&raster)
            .multisample_state(&multi)
            .color_blend_state(&blend)
            .dynamic_state(&dynamic)
            .layout(layout)
            .render_pass(pass);
        let r = d.create_graphics_pipelines(vk::PipelineCache::null(), &[info], None);
        d.destroy_shader_module(vs, None);
        d.destroy_shader_module(fs, None);
        Ok(r.map_err(|e| e.1)?[0])
    }
}
