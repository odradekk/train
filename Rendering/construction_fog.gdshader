shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never;
stencil_mode read, compare_not_equal, 1;

uniform vec4 fog_color : source_color = vec4(0.7882353, 0.8392157, 0.8784314, 1.0);

void fragment() {
    ALBEDO = fog_color.rgb;
    ALPHA = 1.0;
}
