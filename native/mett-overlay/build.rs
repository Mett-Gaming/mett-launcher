//! Uebersetzt den Shader mit naga nach SPIR-V (kein Vulkan SDK noetig).

fn main() {
    println!("cargo:rerun-if-changed=src/quad.wgsl");
    let src = std::fs::read_to_string("src/quad.wgsl").expect("Shader lesen");
    let module =
        naga::front::wgsl::parse_str(&src).unwrap_or_else(|e| panic!("{}", e.emit_to_string(&src)));
    let info = naga::valid::Validator::new(
        naga::valid::ValidationFlags::all(),
        naga::valid::Capabilities::all(),
    )
    .validate(&module)
    .expect("Shader gueltig");
    let out = std::path::PathBuf::from(std::env::var("OUT_DIR").expect("OUT_DIR"));
    for ep in &module.entry_points {
        let pipeline = naga::back::spv::PipelineOptions {
            shader_stage: ep.stage,
            entry_point: ep.name.clone(),
        };
        let words = naga::back::spv::write_vec(
            &module,
            &info,
            &naga::back::spv::Options::default(),
            Some(&pipeline),
        )
        .expect("SPIR-V schreiben");
        let bytes: Vec<u8> = words.iter().flat_map(|w| w.to_le_bytes()).collect();
        std::fs::write(out.join(format!("{}.spv", ep.name)), bytes).expect("Shader schreiben");
    }
}
