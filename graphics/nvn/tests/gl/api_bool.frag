#version 320 es
precision highp float;
#define extern uniform
extern bool uFlag;
uniform bvec2 uPair;
out vec4 color;
void main() { color = uFlag ? vec4(0.0, 1.0, 0.0, 1.0) : vec4(1.0, 0.0, 0.0, 1.0); if (uPair.y) color.b = 1.0; }
