shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx;

// Plan v5 M2 (D9): one sea surface for the whole map at y = 0. Depth comes from the terrain heightfield
// (height_tex, the same samples the ground mesh is built from), not the depth buffer, so it is exact,
// renderer-independent and cheap: turquoise shallows over sand, deep blue offshore, a foam line where the
// sea meets the land. Unexplored water is parchment, like unexplored land.

uniform sampler2D height_tex : filter_linear, repeat_disable;
uniform sampler2D fog_tex : filter_linear, repeat_disable;
uniform sampler2D wave_a : hint_normal, filter_linear_mipmap, repeat_enable;
uniform sampler2D wave_b : hint_normal, filter_linear_mipmap, repeat_enable;
uniform vec4 world_rect;
uniform vec4 shallow_color = vec4(0.32, 0.76, 0.74, 1.0);
uniform vec4 mid_color = vec4(0.12, 0.50, 0.64, 1.0);
uniform vec4 deep_color = vec4(0.05, 0.22, 0.40, 1.0);
uniform vec4 parchment = vec4(0.86, 0.81, 0.69, 1.0);

varying vec3 wpos;

void vertex() {
    wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

float hash12(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float vnoise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash12(i), hash12(i + vec2(1.0, 0.0)), u.x), mix(hash12(i + vec2(0.0, 1.0)), hash12(i + vec2(1.0, 1.0)), u.x), u.y);
}

void fragment() {
    vec2 p = wpos.xz;
    vec2 uv = (p - world_rect.xy) / world_rect.zw;
    bool inside = all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0)));
    float floor_h = inside ? textureLod(height_tex, uv, 0.0).r : -1.4;
    float depth = max(0.0, -floor_h);
    vec2 fog = inside ? textureLod(fog_tex, uv, 0.0).rg : vec2(0.0);

    vec3 col = mix(shallow_color.rgb, mid_color.rgb, smoothstep(0.04, 0.32, depth));
    col = mix(col, deep_color.rgb, smoothstep(0.32, 0.95, depth));

    // Waves: two scrolling noise normal maps, blended.
    vec3 na = texture(wave_a, p * 0.11 + TIME * vec2(0.010, 0.006)).rgb;
    vec3 nb = texture(wave_b, p * 0.23 + TIME * vec2(-0.008, 0.011)).rgb;
    NORMAL_MAP = normalize(mix(na, nb, 0.5));
    NORMAL_MAP_DEPTH = 0.55;

    // Foam where the sea is shallowest, breaking and re-forming slowly.
    float band = 1.0 - smoothstep(0.0, 0.085, depth);
    float n = vnoise(p * 4.0 + TIME * vec2(0.25, 0.18)) * 0.6 + vnoise(p * 9.0 - TIME * 0.3) * 0.4;
    float foam = band * smoothstep(0.42, 0.62, n * 0.8 + band * 0.45);
    col = mix(col, vec3(0.93, 0.97, 0.97), foam * 0.85);

    // Remembered sea is faded; unexplored sea is parchment.
    float grey = dot(col, vec3(0.3, 0.5, 0.2));
    col = mix(mix(vec3(grey), col, 0.62) * vec3(0.76, 0.80, 0.88), col, smoothstep(0.1, 0.9, fog.g));
    float explored = smoothstep(0.15, 0.85, fog.r);
    vec3 paper = parchment.rgb * (0.92 + 0.1 * vnoise(p * 0.6));
    col = mix(paper, col, explored);

    ALBEDO = pow(col, vec3(2.2));
    ROUGHNESS = mix(0.95, 0.07, explored);
    SPECULAR = mix(0.1, 0.55, explored);
    METALLIC = 0.0;
    // Shallows show the sand beneath; parchment and foam are opaque.
    ALPHA = mix(1.0, mix(0.62, 0.96, smoothstep(0.02, 0.45, depth)), explored * (1.0 - foam));
}
