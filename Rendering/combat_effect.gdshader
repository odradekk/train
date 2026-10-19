shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, shadows_disabled, depth_draw_opaque;

uniform float simulation_seconds = 0.0;
uniform int appearance = 0;
instance uniform float start_seconds = 0.0;
instance uniform float strength = 1.0;
instance uniform float seed = 0.0;
instance uniform vec4 tint : source_color = vec4(1.0);
instance uniform float lifetime = 0.0;

varying float effect_alpha;

void vertex() {
	float elapsed = max(simulation_seconds - start_seconds, 0.0);
	bool looping = lifetime <= 0.0;
	float phase = looping ? fract(elapsed * 0.2 + seed) : clamp(elapsed / max(lifetime, 0.001), 0.0, 1.0);
	effect_alpha = strength * (looping ? (1.0 - smoothstep(0.45, 1.0, phase)) : (1.0 - smoothstep(0.72, 1.0, phase)));
	if (appearance <= 2) {
		vec3 origin = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
		float rise = appearance == 0 ? phase * (looping ? 1.8 : 2.4) : appearance == 1 ? 0.12 : phase * 0.35;
		MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
			vec4(INV_VIEW_MATRIX[0].xyz, 0.0),
			vec4(INV_VIEW_MATRIX[1].xyz, 0.0),
			vec4(INV_VIEW_MATRIX[2].xyz, 0.0),
			vec4(origin + vec3(0.0, rise, 0.0), 1.0));
		float size = appearance == 0 ? mix(0.35, 1.15, phase) * (0.45 + strength)
			: appearance == 1 ? 0.28 + strength * 0.35 : 0.08;
		VERTEX *= size;
	}
}

void fragment() {
	vec3 color = tint.rgb;
	if (appearance == 1) color *= 0.75 + 0.25 * sin(simulation_seconds * 6.0 + seed * 20.0);
	ALBEDO = color;
	EMISSION = appearance == 0 || appearance == 5 ? vec3(0.0) : color;
	ALPHA = effect_alpha;
}
