shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;
stencil_mode write, compare_always, 1;

// 默认深度预通道使只有未被实体遮挡的车辆像素写入遮罩。
#define MODEL_HEIGHT
#include "scene_surface.gdshaderinc"
