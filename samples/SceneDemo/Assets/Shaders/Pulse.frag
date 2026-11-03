#version 450 core
// Sprite fragment stage: adds a glow that pulses over time. uTime is supplied by the engine.
in vec2 vTexCoord;
in vec4 vColor;

layout(binding = 0) uniform sampler2D uTexture;
uniform float uTime;
uniform vec4 uGlow;
uniform float uSpeed;

out vec4 fragColor;

void main()
{
    vec4 color = texture(uTexture, vTexCoord) * vColor;
    float pulse = 0.5 + 0.5 * sin(uTime * uSpeed);
    fragColor = vec4(color.rgb + uGlow.rgb * pulse * color.a, color.a);
}
