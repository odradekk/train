shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;

uniform vec4 water_color : source_color = vec4(0.1843, 0.4196, 0.4157, 0.7);

uniform bool wear_enabled = false;
uniform ivec2 wear_seed = ivec2(0);
uniform float surface_damage = 0.0;
uniform bool scorched = false;
varying vec2 texture_uv;
varying vec2 face_uv;
varying flat int exposed_edges;
varying float relative_height;
varying float vertex_damage;

void vertex() {
    texture_uv = UV;
    face_uv = UV2;
    exposed_edges = int(CUSTOM0.x);
    relative_height = CUSTOM0.y;
    vertex_damage = CUSTOM0.z;
}

void fragment() {
    ALBEDO = water_color.rgb;
    ALPHA = water_color.a;
    ROUGHNESS = 0.18;
    METALLIC = 0.0;
    SPECULAR = 0.8;
}
