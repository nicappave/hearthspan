shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx;

// Plan v5 M2 (D9): the continuous ground. The mesh carries height and a baked biome colour (sRGB, in
// COLOR); everything that changes turn to turn arrives as data textures, so a turn costs a few texture
// uploads and never a mesh rebuild:
//   fog_tex   world-space, blurred: R = explored, G = in sight
//   paint_tex world-space: R = road, G = farmland, B = trampled settlement ground
//   hex_data  one texel per hex: R = owner civ index + 1, G = overlay kind, B = flags, A = knowledge
// Hex geometry mirrors game/src/Render/HexMath.cs exactly (pointy-top, odd-r, circumradius 1).

uniform sampler2D fog_tex : filter_linear, repeat_disable;
uniform sampler2D paint_tex : filter_linear, repeat_disable;
uniform sampler2D hex_data : filter_nearest, repeat_disable;
uniform vec4 world_rect;          // min x, min z, width, depth covered by fog/paint
uniform vec2 grid_size;           // hex columns, rows
uniform vec4 civ_colors[8];
uniform float grid_alpha = 0.10;
uniform vec4 selected_hex = vec4(-1.0, -1.0, 0.0, 0.0); // col, row, enabled, unused
uniform vec4 hover_hex = vec4(-1.0, -1.0, 0.0, 0.0);
uniform vec4 parchment = vec4(0.86, 0.81, 0.69, 1.0);
uniform float parchment_height = 0.03;

varying vec3 world_pos;
varying float v_known;

const float SQRT3 = 1.7320508;
const float APOTHEM = 0.8660254;

vec2 fog_at(vec2 p) {
    vec2 uv = (p - world_rect.xy) / world_rect.zw;
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) return vec2(0.0);
    return textureLod(fog_tex, uv, 0.0).rg;
}

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    float known = fog_at(world_pos.xz).r;
    v_known = known;
    // Unexplored ground is a flat parchment sheet: no mountain silhouettes leak through the unknown.
    float k = smoothstep(0.02, 0.75, known);
    VERTEX.y = mix(parchment_height, VERTEX.y, k);
    NORMAL = normalize(mix(vec3(0.0, 1.0, 0.0), NORMAL, k));
    world_pos.y = VERTEX.y;
}

ivec2 hex_of(vec2 p) {
    float q = SQRT3 / 3.0 * p.x - p.y / 3.0;
    float r = 2.0 / 3.0 * p.y;
    vec3 c = vec3(q, -q - r, r);
    vec3 rc = round(c);
    vec3 d = abs(rc - c);
    if (d.x > d.y && d.x > d.z) rc.x = -rc.y - rc.z;
    else if (d.y > d.z) rc.y = -rc.x - rc.z;
    else rc.z = -rc.x - rc.y;
    int aq = int(rc.x);
    int ar = int(rc.z);
    return ivec2(aq + (ar - (ar & 1)) / 2, ar);
}

vec2 hex_center(ivec2 h) {
    return vec2(SQRT3 * (float(h.x) + 0.5 * float(h.y & 1)), 1.5 * float(h.y));
}

vec4 hex_texel(ivec2 h) {
    if (h.x < 0 || h.y < 0 || h.x >= int(grid_size.x) || h.y >= int(grid_size.y)) return vec4(0.0);
    return texelFetch(hex_data, h, 0);
}

ivec2 neighbor(ivec2 h, int dir) {
    bool odd = (h.y & 1) == 1;
    if (dir == 0) return h + ivec2(1, 0);
    if (dir == 1) return h + (odd ? ivec2(1, -1) : ivec2(0, -1));
    if (dir == 2) return h + (odd ? ivec2(0, -1) : ivec2(-1, -1));
    if (dir == 3) return h + ivec2(-1, 0);
    if (dir == 4) return h + (odd ? ivec2(0, 1) : ivec2(-1, 1));
    return h + (odd ? ivec2(1, 1) : ivec2(0, 1));
}

vec2 dir_vec(int dir) {
    if (dir == 0) return vec2(1.0, 0.0);
    if (dir == 1) return vec2(0.5, -APOTHEM);
    if (dir == 2) return vec2(-0.5, -APOTHEM);
    if (dir == 3) return vec2(-1.0, 0.0);
    if (dir == 4) return vec2(-0.5, APOTHEM);
    return vec2(0.5, APOTHEM);
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
    float a = hash12(i), b = hash12(i + vec2(1.0, 0.0));
    float c = hash12(i + vec2(0.0, 1.0)), d = hash12(i + vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

float fbm(vec2 p) {
    float s = 0.0, a = 0.5;
    for (int i = 0; i < 4; i++) {
        s += a * vnoise(p);
        p = p * 2.03 + vec2(17.1, 9.3);
        a *= 0.5;
    }
    return s;
}

vec3 to_linear(vec3 c) { return pow(max(c, vec3(0.0)), vec3(2.2)); }

vec3 overlay_color(int kind) {
    if (kind == 1) return vec3(0.35, 0.85, 0.40);   // good (growth: hamlets can appear)
    if (kind == 2) return vec3(0.95, 0.80, 0.35);   // could, with help
    if (kind == 3) return vec3(0.35, 0.45, 0.75);   // unlikely
    if (kind == 4) return vec3(0.45, 0.95, 0.50);   // legal target (founding)
    if (kind == 5) return vec3(0.45, 0.42, 0.40);   // illegal target
    if (kind == 6) return vec3(0.55, 0.85, 1.00);   // reachable this turn
    if (kind == 7) return vec3(1.00, 0.35, 0.30);   // attack target
    return vec3(0.0);
}

void fragment() {
    vec2 p = world_pos.xz;
    vec2 fog = fog_at(p);
    float known = fog.r;
    float visible = fog.g;

    vec3 world_n = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
    float slope = 1.0 - clamp(world_n.y, 0.0, 1.0);
    float h = world_pos.y;

    // --- ground ------------------------------------------------------------------------------------
    vec3 col = COLOR.rgb;
    float n1 = fbm(p * 0.9);
    float n2 = vnoise(p * 7.0);
    col *= 0.88 + 0.22 * n1 + 0.06 * (n2 - 0.5);
    col = mix(col, col * vec3(1.06, 1.02, 0.86), smoothstep(0.55, 0.8, vnoise(p * 0.35 + 3.1)) * 0.5);

    // beaches: a sandy band where land meets the sea
    float beach = (1.0 - smoothstep(0.035, 0.10, h)) * smoothstep(-0.06, 0.0, h) * (1.0 - smoothstep(0.25, 0.5, slope));
    col = mix(col, vec3(0.84, 0.75, 0.55) * (0.95 + 0.1 * n2), beach);

    // rock on steep ground, snow on the high peaks
    float rock = smoothstep(0.30, 0.55, slope + (n1 - 0.5) * 0.15);
    col = mix(col, vec3(0.46, 0.43, 0.40) * (0.85 + 0.3 * n2), rock * step(0.05, h));
    float snow_line = 1.72 + (n1 - 0.5) * 0.30;
    float snow = smoothstep(snow_line, snow_line + 0.15, h) * (1.0 - smoothstep(0.55, 0.8, slope));
    col = mix(col, vec3(0.94, 0.95, 0.97), snow);

    // --- painted layers: settlement ground, farmland, roads -------------------------------------------
    vec2 puv = (p - world_rect.xy) / world_rect.zw;
    vec4 paint = texture(paint_tex, puv);
    ivec2 hx = hex_of(p);
    vec2 hc = hex_center(hx);
    vec2 local = p - hc;

    col = mix(col, vec3(0.58, 0.51, 0.39) * (0.9 + 0.2 * n2), paint.b * 0.75);

    if (paint.g > 0.01) {
        // Fields: strips in a per-tile direction, alternating ripe grain, green crop and soil.
        float ang = hash12(vec2(hx)) * 3.14159;
        vec2 fd = vec2(cos(ang), sin(ang));
        float stripe = fract(dot(local, fd) * 3.2);
        float band = floor(dot(local, fd) * 3.2);
        float pick = hash12(vec2(band, float(hx.x * 7 + hx.y)));
        vec3 crop = pick < 0.45 ? vec3(0.82, 0.70, 0.33) : (pick < 0.8 ? vec3(0.50, 0.63, 0.27) : vec3(0.52, 0.40, 0.27));
        crop *= 0.9 + 0.2 * vnoise(p * 14.0);
        float furrow = smoothstep(0.0, 0.08, stripe) * smoothstep(1.0, 0.92, stripe);
        crop *= 0.82 + 0.18 * furrow;
        col = mix(col, crop, paint.g * 0.92);
    }

    float road = smoothstep(0.15, 0.55, paint.r);
    col = mix(col, vec3(0.56, 0.45, 0.31) * (0.88 + 0.24 * n2), road * 0.95);

    // --- hex grid, borders, overlays, selection -------------------------------------------------------
    vec4 data = hex_texel(hx);
    int owner = int(round(data.r * 255.0)) - 1;
    int overlay = int(round(data.g * 255.0));
    float hex_known = data.a;

    float max_dot = -1.0;
    for (int d = 0; d < 6; d++) max_dot = max(max_dot, dot(local, dir_vec(d)));
    float edge = APOTHEM - max_dot;                  // distance to this hex's nearest edge
    float aa = fwidth(edge) * 1.2 + 0.002;

    // Faint grid on known land, so tiles stay countable without shouting.
    float grid = 1.0 - smoothstep(0.012, 0.012 + aa, edge);
    col = mix(col, col * 0.55, grid * grid_alpha * step(0.004, hex_known) * step(0.0, h));

    if (overlay > 0 && hex_known > 0.004) {
        vec3 oc = overlay_color(overlay);
        float fill = smoothstep(0.02, 0.10, edge);
        col = mix(col, oc, 0.30 * fill);
        col = mix(col, oc, 0.85 * (1.0 - smoothstep(0.05, 0.05 + aa, edge)));
    }

    // Civ borders: each side of a frontier draws its own colour, a solid line at the edge fading into
    // a wash, Civ-style. Checked against every edge (not only the nearest) so corners join cleanly.
    if (owner >= 0 && hex_known > 0.004) {
        vec3 cc = civ_colors[min(owner, 7)].rgb;
        float line_w = 0.07;
        float border = 0.0;
        float wash = 0.0;
        for (int d = 0; d < 6; d++) {
            float e = APOTHEM - dot(local, dir_vec(d));
            if (e > 0.45) continue;
            vec4 nd = hex_texel(neighbor(hx, d));
            int nowner = int(round(nd.r * 255.0)) - 1;
            if (nowner == owner) continue;
            border = max(border, 1.0 - smoothstep(line_w, line_w + aa, e));
            wash = max(wash, 1.0 - smoothstep(0.0, 0.42, e));
        }
        col = mix(col, cc, wash * 0.28);
        col = mix(col, cc * 1.1, border * 0.92);
    }

    if (selected_hex.z > 0.5 && ivec2(selected_hex.xy) == hx) {
        float ring = 1.0 - smoothstep(0.06, 0.06 + aa, edge);
        float pulse = 0.75 + 0.25 * sin(TIME * 4.0);
        col = mix(col, vec3(1.0, 0.93, 0.55), ring * pulse);
        col = mix(col, vec3(1.0, 0.95, 0.7), 0.10 * smoothstep(0.1, 0.4, edge));
    }
    if (hover_hex.z > 0.5 && ivec2(hover_hex.xy) == hx) {
        float ring = 1.0 - smoothstep(0.035, 0.035 + aa, edge);
        col = mix(col, vec3(1.0, 1.0, 1.0), ring * 0.7);
    }

    // A touch less saturation overall: the stylised palette should read as a painted map, not neon.
    float lum = dot(col, vec3(0.3, 0.55, 0.15));
    col = mix(vec3(lum), col, 0.86);

    // --- fog of war: remembered ground is dimmed, unexplored ground is parchment --------------------------
    float grey = dot(col, vec3(0.3, 0.5, 0.2));
    vec3 remembered = mix(vec3(grey), col, 0.62) * vec3(0.74, 0.76, 0.82);
    col = mix(remembered, col, smoothstep(0.1, 0.9, visible));

    vec3 paper = parchment.rgb * (0.93 + 0.08 * fbm(p * 0.6) + 0.03 * (vnoise(p * 9.0) - 0.5));
    // faint sketched hex outlines on the parchment, like an unfinished survey — fading out when the
    // camera is far enough that they would only be a moire of lines
    float sketch = (1.0 - smoothstep(0.012, 0.012 + aa * 1.2, edge)) * (1.0 - smoothstep(0.01, 0.04, aa));
    paper = mix(paper, paper * 0.90, sketch * 0.5);
    float explored = smoothstep(0.15, 0.85, known);
    col = mix(paper, col, explored);
    // a darker rim where the known world ends
    col *= 1.0 - 0.18 * (1.0 - abs(explored * 2.0 - 1.0)) * step(0.01, known) * step(known, 0.99);

    ALBEDO = to_linear(col);
    ROUGHNESS = mix(0.92, 0.85, beach);
    SPECULAR = 0.25;
}
