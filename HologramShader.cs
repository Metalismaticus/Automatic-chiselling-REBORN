using Vintagestory.API.Client;

namespace AutomaticChiselling
{
    /// <summary>
    /// Custom shader for the hologram preview — animated vertical gradient with
    /// per-face brightness (top brighter, bottom darker, sides shaded).
    /// Gradient colors scroll upward over time.
    /// </summary>
    public static class HologramShader
    {
        public const string Key = "autochisel-hologram";

        public const string VertexCode = @"
#version 330 core
layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 uvIn;
layout(location = 2) in vec4 colorIn;

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;
uniform vec3 uChunkWorldPos;

out vec2 uv;
out vec4 color;
out vec3 localPos;
out vec3 worldPos;

void main()
{
    uv = uvIn;
    color = colorIn;
    localPos = vertexPositionIn;
    worldPos = localPos + uChunkWorldPos;
    gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(localPos, 1.0);
}
";

        public const string FragmentCode = @"
#version 330 core
in vec2 uv;
in vec4 color;
in vec3 localPos;
in vec3 worldPos;

uniform sampler2D tex;
uniform float uMinY;
uniform float uHeight;
uniform float uTime;
uniform vec3 uColorA;
uniform vec3 uColorB;
uniform float uSpeedBlocksPerSec;

out vec4 fragColor;

void main()
{
    vec3 dx = dFdx(localPos);
    vec3 dy = dFdy(localPos);
    vec3 normal = normalize(cross(dx, dy));

    float brightness = 1.0;
    if (abs(normal.y) > 0.5) {
        brightness = (normal.y > 0.0) ? 1.2 : 0.7;
    } else {
        if (abs(normal.x) > abs(normal.z)) brightness = 0.9;
        else brightness = 1.0;
    }

    float localY = worldPos.y - uMinY;
    float phase = localY / max(uHeight, 0.0001);
    float offset = uTime * uSpeedBlocksPerSec / max(uHeight, 0.0001);
    float tGrad = abs(fract(phase + offset) * 2.0 - 1.0);
    vec3 dynamicColor = mix(uColorA, uColorB, tGrad);
    vec4 finalColor = vec4(dynamicColor, color.a);
    finalColor.rgb *= brightness;
    fragColor = finalColor;
}
";

        /// <summary>
        /// Creates and registers the shader program. Call from AssetsLoaded or later.
        /// Returns null if compilation failed (renderer falls back to standard shader).
        /// </summary>
        public static IShaderProgram Register(ICoreClientAPI capi)
        {
            var program = capi.Shader.NewShaderProgram();
            program.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
            program.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
            program.VertexShader.Code = VertexCode;
            program.FragmentShader.Code = FragmentCode;

            capi.Shader.RegisterMemoryShaderProgram(Key, program);

            if (!program.Compile())
            {
                capi.Logger.Warning("[AutoChisel] Hologram shader failed to compile");
                return null;
            }
            return program;
        }
    }
}
