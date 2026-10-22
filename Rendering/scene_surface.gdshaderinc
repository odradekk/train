const int PAINTED_METAL = 0;
const int BARE_METAL = 1;
const int CONCRETE = 2;
const int STONE_SOIL = 3;
const int GLASS = 4;
const int POLYMER_RUBBER = 5;
const int ORGANIC = 6;

uniform sampler2D base_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_texture = false;
uniform vec4 base_color : source_color = vec4(1.0);
uniform vec4 paint_color : source_color = vec4(1.0);
uniform bool has_paint = false;
uniform bool voxel_paint_mask = false;
uniform int material_category = 0;
uniform float material_roughness = 0.6;
uniform float material_metallic = 0.0;
uniform float material_specular = 0.5;
uniform bool rail_steel = false;
uniform vec4 rail_head_color : source_color = vec4(1.0);
uniform bool alpha_cutout = false;
uniform float alpha_cutoff = 0.5;

uniform bool wear_enabled = false;
// 两个有符号整数保存原始 ulong 的低/高 32 位；用 uint(...) 取回位模式。
uniform ivec2 wear_seed = ivec2(0);
uniform float surface_damage = 0.0;
uniform sampler2D damage_mask : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool scorched = false;
// 只有模型变体声明实例变量：Godot 为每个用到实例变量的实例在全局缓冲区预留 16 个 vec4，
// 体素网格每块一个实例，全部预留会超过默认 4096 个实例的上限。
#ifdef MODEL_HEIGHT
const bool model_height = true;
instance uniform vec4 relative_height_plane : instance_index(0) = vec4(0.0);
#else
const bool model_height = false;
#endif

varying vec2 face_uv;
varying flat int exposed_edges;
varying float relative_height;
// 同一面的损伤相同。flat 避免插值后无法判断顶点损伤等于 1。
varying flat float vertex_damage;
varying vec3 local_position;
varying vec3 local_normal;

void vertex() {
    face_uv = UV2;
    exposed_edges = int(CUSTOM0.x);
#ifdef MODEL_HEIGHT
    relative_height = dot(vec4(VERTEX, 1.0), relative_height_plane);
#else
    relative_height = CUSTOM0.y;
#endif
    vertex_damage = CUSTOM0.z;
    local_position = VERTEX;
    local_normal = NORMAL;
}

float hash_value(ivec3 point) {
    uvec3 p = uvec3(point);
    uint value = p.x * 0x9e3779b9u ^ p.y * 0x85ebca6bu ^ p.z * 0xc2b2ae35u;
    value ^= uint(wear_seed.x) ^ uint(wear_seed.y) * 0x27d4eb2du;
    value = (value ^ (value >> 16u)) * 0x7feb352du;
    value = (value ^ (value >> 15u)) * 0x846ca68bu;
    return float(value ^ (value >> 16u)) / 4294967295.0;
}

float mottling(vec3 position) {
    ivec3 cell = ivec3(floor(position));
    vec3 f = fract(position);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(hash_value(cell), hash_value(cell + ivec3(1, 0, 0)), f.x),
                   mix(hash_value(cell + ivec3(0, 1, 0)), hash_value(cell + ivec3(1, 1, 0)), f.x), f.y),
               mix(mix(hash_value(cell + ivec3(0, 0, 1)), hash_value(cell + ivec3(1, 0, 1)), f.x),
                   mix(hash_value(cell + ivec3(0, 1, 1)), hash_value(cell + ivec3(1, 1, 1)), f.x), f.y), f.z);
}

float exposed_edge() {
    float distance = 1.0;
    if ((exposed_edges & 1) != 0) { distance = min(distance, face_uv.x); }
    if ((exposed_edges & 2) != 0) { distance = min(distance, 1.0 - face_uv.x); }
    if ((exposed_edges & 4) != 0) { distance = min(distance, face_uv.y); }
    if ((exposed_edges & 8) != 0) { distance = min(distance, 1.0 - face_uv.y); }
    return 1.0 - smoothstep(0.015, 0.06, distance);
}

void fragment() {
    vec4 color = base_color;
    vec4 texel = has_texture ? texture(base_texture, UV) : vec4(1.0);
    color *= texel;
    float paint_weight = voxel_paint_mask ? texel.a : 1.0;
    if (has_paint && (material_category == PAINTED_METAL || material_category == CONCRETE)) {
        color.rgb *= mix(vec3(1.0), paint_color.rgb, paint_weight);
    }
    float rail_head = clamp(UV.x, 0.0, 1.0);
    if (rail_steel) { color.rgb = mix(color.rgb, rail_head_color.rgb, rail_head); }
    if (alpha_cutout && color.a < alpha_cutoff) { discard; }
    if (wear_enabled && material_category != STONE_SOIL && material_category != ORGANIC) {
        float edge = model_height ? 0.0 : exposed_edge();
        if (material_category == GLASS) {
            color.rgb = mix(color.rgb, vec3(0.28), edge * 0.28);
        } else {
            float variation = mottling(local_position * 0.22);
            color.rgb *= material_category == BARE_METAL ? mix(0.92, 1.0, variation) : mix(0.96, 1.0, variation);
            if (material_category == PAINTED_METAL) {
                float chipped = edge * paint_weight * mix(0.35, 0.7, mottling(local_position * 7.0));
                vec3 substrate = mix(vec3(0.035), vec3(0.23), smoothstep(0.65, 0.95, variation));
                color.rgb = mix(color.rgb, substrate, chipped);
            } else if (material_category == CONCRETE) {
                color.rgb = mix(color.rgb, max(color.rgb, vec3(0.55)), edge * 0.18);
            } else if (material_category == POLYMER_RUBBER) {
                color.rgb = mix(color.rgb, vec3(0.33), 0.03 + variation * 0.04);
            }
            float bottom_dust = 1.0 - smoothstep(0.0, 0.45, clamp(relative_height, 0.0, 1.0));
            float top_dust = model_height ? 0.0 : max(normalize(local_normal).y, 0.0);
            color.rgb = mix(color.rgb, vec3(0.23, 0.20, 0.16), bottom_dust * 0.14 + top_dust * 0.08);
        }
    }
    float damage_amount = clamp(vertex_damage == 1.0 ? surface_damage : vertex_damage, 0.0, 0.65);
    if (damage_amount > 0.0) {
        vec3 axis = abs(local_normal);
        vec2 damage_uv = axis.y >= axis.x && axis.y >= axis.z ? local_position.xz
            : axis.x >= axis.z ? local_position.zy : local_position.xy;
        vec4 damage = texture(damage_mask, damage_uv / 4.0);
        float fine = damage.r;
        vec3 burned = vec3(0.045, 0.038, 0.032);
        if (material_category == PAINTED_METAL) {
            vec3 steel = vec3(0.62, 0.64, 0.66);
            color.rgb = mix(color.rgb, mix(steel, burned, smoothstep(0.4, 0.75, fine)), damage_amount);
        } else if (material_category == BARE_METAL) {
            vec3 heat = mix(vec3(0.45, 0.28, 0.16), burned, smoothstep(0.3, 0.8, fine));
            color.rgb = mix(color.rgb, heat, damage_amount);
        } else if (material_category == CONCRETE) {
            color.rgb = mix(color.rgb, vec3(0.55, 0.52, 0.48), damage_amount * damage.b * 0.7);
            color.rgb = mix(color.rgb, burned, damage_amount * max(damage.g, fine * 0.35));
        } else if (material_category == GLASS) {
            color.rgb = mix(color.rgb, vec3(0.75, 0.82, 0.86), damage_amount * damage.a);
        } else {
            color.rgb = mix(color.rgb, burned, damage_amount * mix(0.55, 1.0, fine));
        }
        if (scorched) color.rgb = mix(color.rgb, burned, damage_amount * 0.45);
    }
    ALBEDO = color.rgb;
    ROUGHNESS = rail_steel ? mix(0.8, material_roughness, rail_head) : material_roughness;
    METALLIC = rail_steel ? mix(0.15, material_metallic, rail_head) : material_metallic;
    SPECULAR = material_specular;
}
