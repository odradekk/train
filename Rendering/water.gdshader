shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;

uniform vec4 water_color : source_color = vec4(0.1843, 0.4196, 0.4157, 0.7);

void fragment() {
    ALBEDO = water_color.rgb;
    ALPHA = water_color.a;
    ROUGHNESS = 0.18;
    METALLIC = 0.0;
    SPECULAR = 0.8;
}
