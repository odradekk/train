shader_type spatial;
render_mode unshaded, cull_disabled;

uniform vec4 fog_color : source_color = vec4(0.7882353, 0.8392157, 0.8784314, 1.0);
uniform bool train_visible = false;
uniform vec3 train_minimum;
uniform vec3 train_maximum;
varying vec3 world_position;

bool intersects_train(vec3 origin, vec3 direction) {
    float near = 0.0;
    float far = 1e20;
    for (int axis = 0; axis < 3; axis++) {
        if (abs(direction[axis]) < 1e-8) {
            if (origin[axis] < train_minimum[axis] || origin[axis] > train_maximum[axis]) return false;
        } else {
            float a = (train_minimum[axis] - origin[axis]) / direction[axis];
            float b = (train_maximum[axis] - origin[axis]) / direction[axis];
            near = max(near, min(a, b));
            far = min(far, max(a, b));
        }
    }
    return far >= near;
}

void vertex() {
    world_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    if (train_visible) {
        vec4 view_near = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, 1.0, 1.0);
        vec3 ray_origin = (INV_VIEW_MATRIX * vec4(view_near.xyz / view_near.w, 1.0)).xyz;
        if (intersects_train(ray_origin, world_position - ray_origin)) discard;
    }
    ALBEDO = fog_color.rgb;
}
