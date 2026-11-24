// Ein Rechteck mit Bild. Farben sind Anzeigewerte, mit Alpha vormultipliziert.

struct Push {
    rect: vec4<f32>,   // links, oben, Breite, Hoehe (-1..1, y nach unten)
    params: vec4<f32>, // Deckkraft, Art (0 SDR, 1 sRGB-Format, 2 HDR10, 3 scRGB), Weiss in Nits
};
var<push_constant> pc: Push;

@group(0) @binding(0) var tex: texture_2d<f32>;
@group(0) @binding(1) var samp: sampler;

struct VsOut {
    @builtin(position) pos: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_main(@builtin(vertex_index) i: u32) -> VsOut {
    let k = vec2<f32>(f32(i & 1u), f32(i >> 1u));
    let p = pc.rect.xy + k * pc.rect.zw;
    var out: VsOut;
    // naga dreht y um: oben im Bild ist +1
    out.pos = vec4<f32>(p.x, -p.y, 0.0, 1.0);
    out.uv = k;
    return out;
}

fn to_linear(c: vec3<f32>) -> vec3<f32> {
    let hi = pow((c + vec3<f32>(0.055)) / 1.055, vec3<f32>(2.4));
    return select(hi, c / 12.92, c <= vec3<f32>(0.04045));
}

fn pq(nits: vec3<f32>) -> vec3<f32> {
    let y = pow(clamp(nits / 10000.0, vec3<f32>(0.0), vec3<f32>(1.0)), vec3<f32>(0.1593017578125));
    return pow((vec3<f32>(0.8359375) + 18.8515625 * y) / (vec3<f32>(1.0) + 18.6875 * y), vec3<f32>(78.84375));
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    let t = textureSampleLevel(tex, samp, in.uv, 0.0);
    let a = t.a * pc.params.x;
    if (pc.params.y < 0.5) {
        return vec4<f32>(t.rgb * pc.params.x, a);
    }
    var c = to_linear(clamp(t.rgb / max(t.a, 0.0001), vec3<f32>(0.0), vec3<f32>(1.0)));
    if (pc.params.y > 2.5) {
        c = c * pc.params.z / 80.0;
    } else if (pc.params.y > 1.5) {
        let n = c * pc.params.z;
        c = pq(vec3<f32>(
            dot(n, vec3<f32>(0.627404, 0.329283, 0.043313)),
            dot(n, vec3<f32>(0.069097, 0.919540, 0.011362)),
            dot(n, vec3<f32>(0.016391, 0.088013, 0.895595))));
    }
    return vec4<f32>(c * a, a);
}
