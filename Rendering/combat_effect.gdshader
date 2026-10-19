shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, shadows_disabled, depth_draw_opaque;

uniform float simulation_seconds = 0.0;
uniform float flicker_seconds = 0.0;
uniform int appearance = 0;
instance uniform float start_seconds = 0.0;
instance uniform float strength = 1.0;
instance uniform float seed = 0.0;
instance uniform vec4 tint : source_color = vec4(1.0);
instance uniform float lifetime = 0.0;

varying float effect_alpha;

void vertex() {
	float elapsed = max(simulation_seconds - start_seconds, 0.0);
	float phase = lifetime <= 0.0 ? 1.0 : clamp(elapsed / max(lifetime, 0.001), 0.0, 1.0);
	effect_alpha = strength * (1.0 - smoothstep(0.72, 1.0, phase));
	if (appearance <= 2) {
		float index = UV2.x;
		float count = max(UV2.y, 1.0);
		bool looping = lifetime <= 0.0;
		float local = looping
			? fract(elapsed * 0.35 + seed + index / count)
			: clamp((phase - index / count * 0.45) / 0.55, 0.0, 1.0);
		// 相位两端都是 0，循环回到起点时不会突然以满透明度出现。
		effect_alpha = strength * smoothstep(0.0, 0.18, local) * (1.0 - smoothstep(0.62, 1.0, local));
		float node_scale = max(length(MODEL_MATRIX[0].xyz), 0.001);
		float rise = (appearance == 0 ? local * (looping ? 1.6 : 2.2)
			: appearance == 1 ? local * 0.35 : local * 0.7) * node_scale;
		float angle = index * 2.399963 + seed * 6.283185;
		float spread = (appearance == 2 ? 0.2 : 0.16) * node_scale;
		vec3 scatter = vec3(cos(angle), 0.0, sin(angle)) * spread * (looping ? 1.0 : local);
		float size = (appearance == 0 ? mix(0.4, 1.1, local) * (0.5 + strength * 0.45)
			: appearance == 1 ? 0.42 + strength * 0.38 : 0.1) * node_scale;
		vec3 origin = (MODEL_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
		MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
			vec4(INV_VIEW_MATRIX[0].xyz, 0.0),
			vec4(INV_VIEW_MATRIX[1].xyz, 0.0),
			vec4(INV_VIEW_MATRIX[2].xyz, 0.0),
			vec4(origin + scatter + vec3(0.0, rise, 0.0), 1.0));
		VERTEX = vec3((UV - vec2(0.5)) * size, 0.0);
	}
}

void fragment() {
	vec3 color = tint.rgb;
	// flicker_seconds 不快于真实时间；6 rad/s 约每秒变亮一次。
	if (appearance == 1) color *= 0.75 + 0.25 * sin(flicker_seconds * 6.0 + seed * 20.0);
	float disk = appearance <= 2 ? 1.0 - smoothstep(0.15, 0.5, length(UV - vec2(0.5))) : 1.0;
	ALBEDO = color;
	EMISSION = appearance == 0 || appearance == 5 ? vec3(0.0) : color;
	ALPHA = effect_alpha * disk;
}
