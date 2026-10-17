shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;

uniform sampler2D base_texture : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_texture = false;
uniform vec4 base_color : source_color = vec4(1.0);
uniform vec4 paint_color : source_color = vec4(1.0);
uniform bool has_paint = false;
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
uniform bool scorched = false;
uniform bool model_height = false;
uniform vec4 relative_height_plane = vec4(0.0);

varying vec2 face_uv;
varying flat int exposed_edges;
varying float relative_height;
varying float vertex_damage;

void vertex() {
    face_uv = UV2;
    exposed_edges = int(CUSTOM0.x);
    relative_height = model_height ? dot(vec4(VERTEX, 1.0), relative_height_plane) : CUSTOM0.y;
    vertex_damage = CUSTOM0.z;
}

void fragment() {
    vec4 color = base_color;
    if (has_texture) { color *= texture(base_texture, UV); }
    if (has_paint) { color.rgb *= paint_color.rgb; }
    float rail_head = clamp(UV.x, 0.0, 1.0);
    if (rail_steel) { color.rgb = mix(color.rgb, rail_head_color.rgb, rail_head); }
    if (alpha_cutout && color.a < alpha_cutoff) { discard; }
    ALBEDO = color.rgb;
    ROUGHNESS = rail_steel ? mix(0.8, material_roughness, rail_head) : material_roughness;
    METALLIC = rail_steel ? mix(0.15, material_metallic, rail_head) : material_metallic;
    SPECULAR = material_specular;
    // 上面的表现通道和表面状态供 #339 使用，本票不叠加正式磨损。
}
