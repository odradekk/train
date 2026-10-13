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
    if (alpha_cutout && color.a < alpha_cutoff) { discard; }
    ALBEDO = color.rgb;
    ROUGHNESS = material_roughness;
    METALLIC = material_metallic;
    SPECULAR = material_specular;
    // 上面的表现通道和表面状态供 #339 使用，本票不叠加正式磨损。
}
