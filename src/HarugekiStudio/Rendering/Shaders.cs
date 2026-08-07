using Avalonia.OpenGL;

namespace HarugekiStudio.Rendering;

/// <summary>
/// GLSL for the three programs the viewport runs: the model, the ground grid and
/// the origin axes.
///
/// <para>
/// The sources carry no <c>#version</c> line of their own. <see cref="Header"/>
/// supplies it at compile time from the context Avalonia actually gave us, so one
/// copy of each shader serves both desktop GL and GLES rather than two that can
/// drift apart.
/// </para>
/// </summary>
internal static class Shaders
{
    /// <summary>The version and precision preamble for the live context.</summary>
    public static string Header(GlVersion version)
    {
        return version.Type == GlProfileType.OpenGLES
            ? "#version 300 es\nprecision highp float;\n"
            : "#version 330 core\n";
    }

    public const string ModelVertex = """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        layout(location = 3) in vec4 aColor;
        uniform mat4 uMvp;
        uniform mat4 uView;
        out vec3 vNormal;
        out vec2 vUv;
        out vec4 vColor;
        void main()
        {
            vNormal = mat3(uView) * aNormal;
            vUv = aUv;
            vColor = aColor;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    /// <summary>
    /// The uMode branches match <see cref="ShadingMode"/> by ordinal. Faces are
    /// authored two-sided, so the normal is flipped for back faces rather than
    /// culled.
    /// </summary>
    public const string ModelFragment = """
        uniform sampler2D uTex;
        uniform int uMode;
        uniform float uHasTex;
        in vec3 vNormal;
        in vec2 vUv;
        in vec4 vColor;
        out vec4 fragColor;
        void main()
        {
            vec3 n = normalize(vNormal);
            if (!gl_FrontFacing) n = -n;
            float lambert = 0.28 + 0.72 * max(n.z, 0.0);

            if (uMode == 3) { fragColor = vec4(n * 0.5 + 0.5, 1.0); return; }
            if (uMode == 4) { fragColor = vec4(fract(vUv), 0.0, 1.0); return; }
            if (uMode == 2) { fragColor = vec4(0.55, 0.85, 1.0, 1.0); return; }

            vec4 base = uHasTex > 0.5 ? texture(uTex, vUv) : vec4(0.78, 0.78, 0.80, 1.0);
            if (uMode == 1) base = vec4(0.78, 0.78, 0.80, 1.0);
            fragColor = vec4(base.rgb * lambert, 1.0);
        }
        """;

    public const string FlatVertex = """
        layout(location = 0) in vec3 aPos;
        uniform mat4 uMvp;
        void main() { gl_Position = uMvp * vec4(aPos, 1.0); }
        """;

    /// <summary>
    /// A single flat colour, supplied as three separate floats.
    ///
    /// <para>
    /// Three <c>float</c> uniforms rather than one <c>vec4</c> because
    /// <c>GlInterface</c> exposes <c>Uniform1f</c> but has no <c>Uniform4f</c> at
    /// all. They cannot be compiled into the shader either: the grid follows the
    /// theme, so the colour has to be settable without rebuilding the program.
    /// </para>
    /// </summary>
    public const string FlatFragment = """
        uniform float uR;
        uniform float uG;
        uniform float uB;
        out vec4 fragColor;
        void main() { fragColor = vec4(uR, uG, uB, 1.0); }
        """;

    public const string VertexColorVertex = """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aColor;
        uniform mat4 uMvp;
        out vec3 vColor;
        void main() { vColor = aColor; gl_Position = uMvp * vec4(aPos, 1.0); }
        """;

    public const string VertexColorFragment = """
        in vec3 vColor;
        out vec4 fragColor;
        void main() { fragColor = vec4(vColor, 1.0); }
        """;
}
