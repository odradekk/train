// Adapted from Godot 4.7.2 ProceduralSkyMaterial::_update_shader.
// https://github.com/godotengine/godot/blob/4.7.2-stable/scene/resources/3d/sky_material.cpp
// Copyright (c) 2014-present Godot Engine contributors (see AUTHORS.md).
// Copyright (c) 2007-2014 Juan Linietsky, Ariel Manzur.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
// of the Software, and to permit persons to whom the Software is furnished to do
// so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

shader_type sky;
render_mode use_debanding;

uniform vec4 sky_top_color : source_color;
uniform vec4 sky_horizon_color : source_color = vec4(0.788235294117647, 0.83921568627451, 0.87843137254902, 1.0);
uniform float inv_sky_curve = 3.999999761581421;
uniform vec4 ground_bottom_color : source_color = vec4(0.2, 0.169, 0.133, 1.0);
uniform vec4 ground_horizon_color : source_color = vec4(0.788235294117647, 0.83921568627451, 0.87843137254902, 1.0);
uniform float inv_ground_curve = 30.0;
uniform float sun_angle_max = 0.8660253882408142;
uniform float inv_sun_curve = 22.78201675415039;
uniform float exposure = 1.0;

void sky() {
    float v_angle = clamp(EYEDIR.y, -1.0, 1.0);
    vec3 sky = mix(sky_top_color.rgb, sky_horizon_color.rgb, clamp(pow(1.0 - v_angle, inv_sky_curve), 0.0, 1.0));

    if (LIGHT0_ENABLED) {
        float sun_angle = dot(LIGHT0_DIRECTION, EYEDIR);
        float sun_size = cos(LIGHT0_SIZE);
        if (sun_angle > sun_size) {
            sky = LIGHT0_COLOR * LIGHT0_ENERGY;
        } else if (sun_angle > sun_angle_max) {
            float c2 = (sun_size - sun_angle) / (sun_size - sun_angle_max);
            sky = mix(sky, LIGHT0_COLOR * LIGHT0_ENERGY, clamp(pow(1.0 - c2, inv_sun_curve), 0.0, 1.0));
        }
    }

    vec3 ground = mix(ground_bottom_color.rgb, ground_horizon_color.rgb, clamp(pow(1.0 + v_angle, inv_ground_curve), 0.0, 1.0));
    COLOR = mix(ground, sky, step(0.0, EYEDIR.y)) * exposure;
    // 背景接上地形雾；辐射仍使用原来的深色地面，保留已确认的环境光和反射。
    if (!AT_CUBEMAP_PASS && EYEDIR.y < 0.0) {
        COLOR = ground_horizon_color.rgb;
    }
}
