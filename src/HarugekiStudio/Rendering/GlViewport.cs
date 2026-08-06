using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Harugeki.Formats;
using System.Numerics;
using System.Runtime.InteropServices;
using static Avalonia.OpenGL.GlConsts;

namespace HarugekiStudio.Rendering;

public enum ShadingMode { Textured, Shaded, Wireframe, Normals, Uv }

/// <summary>Constants Avalonia's GlConsts does not define.</summary>
internal static class Gl
{
    public const int Lines = 0x0001;
    public const int Repeat = 0x2901;
}

/// <summary>
/// Hardware viewport built on Avalonia's own GL binding, so it needs no external
/// GL package. Geometry is uploaded once per model and drawn with glDrawArrays:
/// the file already stores a plain triangle list.
/// </summary>
public class GlViewport : OpenGlControlBase
{
    public static readonly StyledProperty<RingModel?> ModelProperty =
        AvaloniaProperty.Register<GlViewport, RingModel?>(nameof(Model));

    public static readonly StyledProperty<ShadingMode> ShadingProperty =
        AvaloniaProperty.Register<GlViewport, ShadingMode>(nameof(Shading));

    public RingModel? Model
    {
        get => GetValue(ModelProperty);
        set { _ = SetValue(ModelProperty, value); _dirty = true; RequestNextFrameRendering(); }
    }

    public ShadingMode Shading
    {
        get => GetValue(ShadingProperty);
        set => SetValue(ShadingProperty, value);
    }

    static GlViewport()
    {
        _ = ShadingProperty.Changed.AddClassHandler<GlViewport>((v, _) => v.RequestNextFrameRendering());
        _ = ModelProperty.Changed.AddClassHandler<GlViewport>((v, _) => { v._dirty = true; v.RequestNextFrameRendering(); });
    }

    // ---- camera ----------------------------------------------------------
    private float _yaw = 0.6f, _pitch = 0.25f, _distance = 300f, _targetDistance = 300f;
    private bool _zoomAnimating;
    private Vector3 _target = new(0, 80, 0);
    private bool _dirty = true;

    // ---- gl objects ------------------------------------------------------
    private int _program, _vao, _lineVao, _vbo, _lineVbo, _white;
    private int _uMvp, _uView, _uMode, _uHasTex, _uTex;
    private int _vertexCount, _lineVertexCount;
    private int _gridProgram, _gridVao, _gridVbo, _gridMvp, _gridColor;
    private int _gridVertexCount;
    private int _axisProgram, _axisVao, _axisVbo, _axisMvp;
    private int _axisVertexCount;
    private readonly List<(int First, int Count, int Texture)> _batches = [];
    private readonly List<int> _textures = [];
    private bool _ready;
    private float[]? _animPositions;
    private float[]? _animNormals;

    // ---- per-frame scratch (pre-allocated to avoid heap pressure) --------
    private readonly float[] _matScratch = new float[16];
    private readonly object?[] _matArgs = new object?[4];

    private static readonly System.Reflection.MethodInfo? s_uniformMatrix4fv =
        typeof(GlInterface).GetMethod("UniformMatrix4fv",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

    private static readonly System.Reflection.MethodInfo? s_uniform4f =
        typeof(GlInterface).GetMethod("Uniform4f",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

    public GlViewport()
    {
        Focusable = true;
        ClipToBounds = true;
        _matArgs[1] = 1;
        _matArgs[2] = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_ready) { _dirty = true; RequestNextFrameRendering(); }
    }

    public void Orbit(double dx, double dy)
    {
        _yaw -= (float)dx * 0.01f;
        _pitch = Math.Clamp(_pitch + ((float)dy * 0.01f), -1.5f, 1.5f);
        RequestNextFrameRendering();
    }

    public void Pan(double dx, double dy)
    {
        float s = _distance * 0.0015f;
        Vector3 right = new(MathF.Cos(_yaw), 0, -MathF.Sin(_yaw));
        _target += (right * (float)-dx * s) + (new Vector3(0, 1, 0) * (float)dy * s);
        RequestNextFrameRendering();
    }

    public void Zoom(float delta)
    {
        _targetDistance = Math.Clamp(_targetDistance * (1f - (delta * 0.08f)), 1f, 100000f);
        _zoomAnimating = true;
        RequestNextFrameRendering();
    }

    /// <summary>Frames the current model.</summary>
    public void ResetCamera()
    {
        RingModel? model = Model;
        if (model is null || model.Meshes.Count == 0)
        {
            return;
        }

        float minY = float.MaxValue, maxY = float.MinValue, extent = 1f;
        foreach (RingMesh m in model.Meshes)
        {
            for (int i = 0; i < m.VertexCount; i++)
            {
                float y = m.Positions[(i * 3) + 1];
                minY = MathF.Min(minY, y);
                maxY = MathF.Max(maxY, y);
                extent = MathF.Max(extent, MathF.Abs(m.Positions[i * 3]));
                extent = MathF.Max(extent, MathF.Abs(m.Positions[(i * 3) + 2]));
            }
        }

        _target = new Vector3(0, (minY + maxY) * 0.5f, 0);
        _distance = _targetDistance = MathF.Max(maxY - minY, extent * 2f) * 1.6f;
        _yaw = 0.6f;
        _pitch = 0.15f;
        RequestNextFrameRendering();
    }

    // ---- gl lifecycle ----------------------------------------------------
    protected override void OnOpenGlInit(GlInterface gl)
    {
        if (s_uniformMatrix4fv is null)
        {
            throw new InvalidOperationException(
                "GlInterface.UniformMatrix4fv not found — 3D rendering unavailable.");
        }

        bool es = GlVersion.Type == GlProfileType.OpenGLES;
        string head = es ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";

        int vs = gl.CreateShader(GL_VERTEX_SHADER);
        Check(gl.CompileShaderAndGetError(vs, head + VertexSource), "vertex shader");
        int fs = gl.CreateShader(GL_FRAGMENT_SHADER);
        Check(gl.CompileShaderAndGetError(fs, head + FragmentSource), "fragment shader");

        _program = gl.CreateProgram();
        gl.AttachShader(_program, vs);
        gl.AttachShader(_program, fs);
        Check(gl.LinkProgramAndGetError(_program), "program link");
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);

        _uMvp = gl.GetUniformLocationString(_program, "uMvp");
        _uView = gl.GetUniformLocationString(_program, "uView");
        _uMode = gl.GetUniformLocationString(_program, "uMode");
        _uHasTex = gl.GetUniformLocationString(_program, "uHasTex");
        _uTex = gl.GetUniformLocationString(_program, "uTex");

        _vao = gl.GenVertexArray();
        _lineVao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        _lineVbo = gl.GenBuffer();
        _white = MakeSolidTexture(gl, 0xFFFFFFFF);

        _gridVao = gl.GenVertexArray();
        _gridVbo = gl.GenBuffer();
        int gvs = gl.CreateShader(GL_VERTEX_SHADER);
        Check(gl.CompileShaderAndGetError(gvs, head + GridVertexSource), "grid vertex shader");
        int gfs = gl.CreateShader(GL_FRAGMENT_SHADER);
        Check(gl.CompileShaderAndGetError(gfs, head + GridFragmentSource), "grid fragment shader");
        _gridProgram = gl.CreateProgram();
        gl.AttachShader(_gridProgram, gvs);
        gl.AttachShader(_gridProgram, gfs);
        Check(gl.LinkProgramAndGetError(_gridProgram), "grid program link");
        gl.DeleteShader(gvs);
        gl.DeleteShader(gfs);
        _gridMvp = gl.GetUniformLocationString(_gridProgram, "uMvp");
        _gridColor = gl.GetUniformLocationString(_gridProgram, "uColor");

        BuildGrid(gl);

        _axisVao = gl.GenVertexArray();
        _axisVbo = gl.GenBuffer();
        int avs = gl.CreateShader(GL_VERTEX_SHADER);
        Check(gl.CompileShaderAndGetError(avs, head + AxisVertexSource), "axis vertex shader");
        int afs = gl.CreateShader(GL_FRAGMENT_SHADER);
        Check(gl.CompileShaderAndGetError(afs, head + AxisFragmentSource), "axis fragment shader");
        _axisProgram = gl.CreateProgram();
        gl.AttachShader(_axisProgram, avs);
        gl.AttachShader(_axisProgram, afs);
        Check(gl.LinkProgramAndGetError(_axisProgram), "axis program link");
        gl.DeleteShader(avs);
        gl.DeleteShader(afs);
        _axisMvp = gl.GetUniformLocationString(_axisProgram, "uMvp");

        BuildAxes(gl);

        _ready = true;
        _dirty = true;
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (!_ready)
        {
            return;
        }

        ReleaseModel(gl);
        gl.DeleteBuffer(_vbo);
        gl.DeleteBuffer(_lineVbo);
        gl.DeleteVertexArray(_vao);
        gl.DeleteVertexArray(_lineVao);
        gl.DeleteTexture(_white);
        gl.DeleteProgram(_program);
        gl.DeleteBuffer(_gridVbo);
        gl.DeleteVertexArray(_gridVao);
        gl.DeleteProgram(_gridProgram);
        gl.DeleteBuffer(_axisVbo);
        gl.DeleteVertexArray(_axisVao);
        gl.DeleteProgram(_axisProgram);
        _program = _vao = _lineVao = _vbo = _lineVbo = _white = 0;
        _uMvp = _uView = _uMode = _uHasTex = _uTex = 0;
        _vertexCount = _lineVertexCount = 0;
        _batches.Clear();
        _gridProgram = _gridVao = _gridVbo = _gridMvp = _gridColor = 0;
        _gridVertexCount = 0;
        _axisProgram = _axisVao = _axisVbo = _axisMvp = 0;
        _axisVertexCount = 0;
        _animPositions = null;
        _animNormals = null;
        _ready = false;
        _dirty = true;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_zoomAnimating)
        {
            _distance += (_targetDistance - _distance) * 0.25f;
            if (Math.Abs(_distance - _targetDistance) < 0.1f)
            {
                _distance = _targetDistance;
                _zoomAnimating = false;
            }
            else
            {
                RequestNextFrameRendering();
            }
        }

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)(Bounds.Width * scaling));
        int h = Math.Max(1, (int)(Bounds.Height * scaling));

        gl.Viewport(0, 0, w, h);
        gl.ClearColor(0.16f, 0.16f, 0.17f, 1f);
        gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
        gl.Enable(GL_DEPTH_TEST);
        gl.DepthFunc(GL_LESS);
        gl.DepthMask(1);
        // The game's meshes are authored two-sided; culling would punch holes.
        gl.Disable(GL_CULL_FACE);

        if (_dirty) { Upload(gl); _dirty = false; }

        Matrix4x4 view = Matrix4x4.CreateLookAt(EyePosition(), _target, Vector3.UnitY);
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(
            0.9f, w / (float)h, MathF.Max(0.05f, _distance * 0.002f), (_distance * 20f) + 5000f);
        Matrix4x4 mvp = view * proj;

        if (_gridVertexCount > 0)
        {
            gl.DepthMask(0);
            gl.UseProgram(_gridProgram);
            UniformMatrix(gl, _gridMvp, mvp);
            Uniform4f(gl, _gridColor, 0.35f, 0.35f, 0.35f, 1f);
            gl.BindVertexArray(_gridVao);
            gl.DrawArrays(Gl.Lines, 0, _gridVertexCount);
            gl.DepthMask(1);
        }

        if (_axisVertexCount > 0)
        {
            gl.UseProgram(_axisProgram);
            UniformMatrix(gl, _axisMvp, mvp);
            gl.BindVertexArray(_axisVao);
            gl.DrawArrays(Gl.Lines, 0, _axisVertexCount);
        }

        if (_animPositions is not null && _animNormals is not null)
        {
            ApplyAnimatedFrame(gl);
        }

        if (_vertexCount == 0)
        {
            return;
        }

        gl.UseProgram(_program);
        UniformMatrix(gl, _uMvp, mvp);
        UniformMatrix(gl, _uView, view);
        gl.Uniform1i(_uTex, 0);

        if (Shading == ShadingMode.Wireframe)
        {
            // GL_LINES instead of glPolygonMode: the polygon-mode entry point does not
            // exist in GLES and would silently do nothing under ANGLE.
            gl.Uniform1i(_uMode, (int)ShadingMode.Wireframe);
            gl.Uniform1f(_uHasTex, 0f);
            gl.BindVertexArray(_lineVao);
            gl.DrawArrays(Gl.Lines, 0, _lineVertexCount);
        }
        else
        {
            gl.BindVertexArray(_vao);
            gl.Uniform1i(_uMode, (int)Shading);
            foreach ((int first, int count, int tex) in _batches)
            {
                bool textured = Shading == ShadingMode.Textured && tex != 0;
                gl.ActiveTexture(GL_TEXTURE0);
                gl.BindTexture(GL_TEXTURE_2D, textured ? tex : _white);
                gl.Uniform1f(_uHasTex, textured ? 1f : 0f);
                gl.DrawArrays(GL_TRIANGLES, first, count);
            }
        }

        gl.BindVertexArray(0);
        gl.UseProgram(0);
    }

    private Vector3 EyePosition()
    {
        float cp = MathF.Cos(_pitch);
        return _target + (new Vector3(
            MathF.Sin(_yaw) * cp, MathF.Sin(_pitch), MathF.Cos(_yaw) * cp) * _distance);
    }

    // ---- geometry upload -------------------------------------------------
    private const int Stride = 36;   // pos 12 + normal 12 + uv 8 + colour 4

    private void Upload(GlInterface gl)
    {
        ReleaseModel(gl);
        _batches.Clear();
        _vertexCount = _lineVertexCount = 0;

        RingModel? model = Model;
        if (model is null || model.Meshes.Count == 0)
        {
            return;
        }

        foreach (RingTexture tex in model.Textures)
        {
            _textures.Add(MakeTexture(gl, tex));
        }

        int total = model.Meshes.Sum(m => m.VertexCount);
        byte[] verts = new byte[total * Stride];
        byte[] lines = new byte[total * 2 * Stride];
        int v = 0, l = 0;

        foreach (RingMesh mesh in model.Meshes)
        {
            int tri = 0;
            for (int mi = 0; mi < mesh.TriangleCounts.Length; mi++)
            {
                int count = mesh.TriangleCounts[mi];
                int batchFirst = v;

                for (int f = 0; f < count; f++, tri++)
                {
                    foreach (int idx in (ReadOnlySpan<int>)[tri * 3, (tri * 3) + 1, (tri * 3) + 2])
                    {
                        WriteVertex(verts, v++ * Stride, mesh, idx);
                    }

                    int p0 = (v - 3) * Stride, p1 = (v - 2) * Stride, p2 = (v - 1) * Stride;
                    foreach (int e in (ReadOnlySpan<int>)[p0, p1, p1, p2, p2, p0])
                    {
                        Array.Copy(verts, e, lines, l, Stride);
                        l += Stride;
                    }
                }

                int texId = 0;
                if (mi < mesh.Materials.Count)
                {
                    RingMaterial? mat = mesh.Materials[mi];
                    if (mat?.HasTexture == true && mat.TextureIndex < _textures.Count)
                    {
                        texId = _textures[(int)mat.TextureIndex];
                    }
                }
                if (v > batchFirst)
                {
                    _batches.Add((batchFirst, v - batchFirst, texId));
                }
            }
        }

        _vertexCount = v;
        _lineVertexCount = l / Stride;

        gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
        BufferData(gl, verts, _vertexCount * Stride);
        gl.BindBuffer(GL_ARRAY_BUFFER, _lineVbo);
        BufferData(gl, lines, _lineVertexCount * Stride);
        gl.BindBuffer(GL_ARRAY_BUFFER, 0);

        gl.BindVertexArray(_vao);
        BindAttribs(gl, _vbo);
        gl.BindVertexArray(_lineVao);
        BindAttribs(gl, _lineVbo);
        gl.BindVertexArray(0);

        ResetCamera();
    }

    public void SetAnimatedFrame(float[]? positions, float[]? normals)
    {
        _animPositions = positions;
        _animNormals = normals;
        RequestNextFrameRendering();
    }

    private void ApplyAnimatedFrame(GlInterface gl)
    {
        if (_animPositions is null || _animNormals is null) return;
        RingModel? model = Model;
        if (model is null || model.Meshes.Count == 0) return;

        int total = model.Meshes.Sum(m => m.VertexCount);
        byte[] verts = new byte[total * Stride];
        byte[] lines = new byte[total * 2 * Stride];
        int v = 0, l = 0;
        int posOffset = 0, nrmOffset = 0;

        foreach (RingMesh mesh in model.Meshes)
        {
            int tri = 0;
            for (int mi = 0; mi < mesh.TriangleCounts.Length; mi++)
            {
                for (int f = 0; f < mesh.TriangleCounts[mi]; f++, tri++)
                {
                    foreach (int idx in (ReadOnlySpan<int>)[tri * 3, (tri * 3) + 1, (tri * 3) + 2])
                    {
                        WriteVertexAnimated(verts, v++ * Stride, mesh, idx, ref posOffset, ref nrmOffset);
                    }

                    int p0 = (v - 3) * Stride, p1 = (v - 2) * Stride, p2 = (v - 1) * Stride;
                    foreach (int e in (ReadOnlySpan<int>)[p0, p1, p1, p2, p2, p0])
                    {
                        Array.Copy(verts, e, lines, l, Stride);
                        l += Stride;
                    }
                }
            }
        }

        gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
        BufferData(gl, verts, total * Stride);
        gl.BindBuffer(GL_ARRAY_BUFFER, _lineVbo);
        BufferData(gl, lines, total * 2 * Stride);
        gl.BindBuffer(GL_ARRAY_BUFFER, 0);
    }

    private static void WriteVertex(byte[] dst, int at, RingMesh mesh, int i)
    {
        Span<byte> s = dst.AsSpan(at);
        _ = BitConverter.TryWriteBytes(s, mesh.Positions[i * 3]);
        _ = BitConverter.TryWriteBytes(s[4..], mesh.Positions[(i * 3) + 1]);
        _ = BitConverter.TryWriteBytes(s[8..], mesh.Positions[(i * 3) + 2]);
        _ = BitConverter.TryWriteBytes(s[12..], mesh.Normals[i * 3]);
        _ = BitConverter.TryWriteBytes(s[16..], mesh.Normals[(i * 3) + 1]);
        _ = BitConverter.TryWriteBytes(s[20..], mesh.Normals[(i * 3) + 2]);
        _ = BitConverter.TryWriteBytes(s[24..], mesh.Uvs[i * 2]);
        _ = BitConverter.TryWriteBytes(s[28..], mesh.Uvs[(i * 2) + 1]);
        s[32] = mesh.Colors[i * 4];
        s[33] = mesh.Colors[(i * 4) + 1];
        s[34] = mesh.Colors[(i * 4) + 2];
        s[35] = mesh.Colors[(i * 4) + 3];
    }

    private void WriteVertexAnimated(byte[] dst, int at, RingMesh mesh, int i, ref int posOffset, ref int nrmOffset)
    {
        Span<byte> s = dst.AsSpan(at);
        _ = BitConverter.TryWriteBytes(s, _animPositions![posOffset]);
        _ = BitConverter.TryWriteBytes(s[4..], _animPositions[posOffset + 1]);
        _ = BitConverter.TryWriteBytes(s[8..], _animPositions[posOffset + 2]);
        _ = BitConverter.TryWriteBytes(s[12..], _animNormals![nrmOffset]);
        _ = BitConverter.TryWriteBytes(s[16..], _animNormals[nrmOffset + 1]);
        _ = BitConverter.TryWriteBytes(s[20..], _animNormals[nrmOffset + 2]);
        _ = BitConverter.TryWriteBytes(s[24..], mesh.Uvs[i * 2]);
        _ = BitConverter.TryWriteBytes(s[28..], mesh.Uvs[(i * 2) + 1]);
        s[32] = mesh.Colors[i * 4];
        s[33] = mesh.Colors[(i * 4) + 1];
        s[34] = mesh.Colors[(i * 4) + 2];
        s[35] = mesh.Colors[(i * 4) + 3];
        posOffset += 3;
        nrmOffset += 3;
    }

    private static void BindAttribs(GlInterface gl, int buffer)
    {
        gl.BindBuffer(GL_ARRAY_BUFFER, buffer);
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, Stride, 0);
        gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, Stride, 12);
        gl.VertexAttribPointer(2, 2, GL_FLOAT, 0, Stride, 24);
        gl.VertexAttribPointer(3, 4, GL_UNSIGNED_BYTE, 1, Stride, 32);
        for (int i = 0; i < 4; i++)
        {
            gl.EnableVertexAttribArray(i);
        }
    }

    private void ReleaseModel(GlInterface gl)
    {
        foreach (int t in _textures)
        {
            gl.DeleteTexture(t);
        }

        _textures.Clear();
    }

    private void BuildGrid(GlInterface gl)
    {
        const int range = 2000;
        const int step = 20;
        var positions = new List<float>(1700);

        for (int x = -range; x <= range; x += step)
        {
            positions.Add(x); positions.Add(0); positions.Add(-range);
            positions.Add(x); positions.Add(0); positions.Add(range);
        }

        for (int z = -range; z <= range; z += step)
        {
            positions.Add(-range); positions.Add(0); positions.Add(z);
            positions.Add(range); positions.Add(0); positions.Add(z);
        }

        _gridVertexCount = positions.Count / 3;
        byte[] data = new byte[positions.Count * 4];
        Buffer.BlockCopy(positions.ToArray(), 0, data, 0, data.Length);

        gl.BindBuffer(GL_ARRAY_BUFFER, _gridVbo);
        BufferData(gl, data, data.Length);

        gl.BindVertexArray(_gridVao);
        gl.BindBuffer(GL_ARRAY_BUFFER, _gridVbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, 12, 0);
        gl.BindVertexArray(0);
        gl.BindBuffer(GL_ARRAY_BUFFER, 0);
    }

    private void BuildAxes(GlInterface gl)
    {
        const float length = 100f;
        float[] positions = new float[]
        {
            0f, 0f, 0f,  1f, 0f, 0f,  length, 0f, 0f,  1f, 0f, 0f,
            0f, 0f, 0f,  0f, 1f, 0f,  0f, length, 0f,  0f, 1f, 0f,
            0f, 0f, 0f,  0f, 0f, 1f,  0f, 0f, -length,  0f, 0f, 1f,
        };

        _axisVertexCount = positions.Length / 6;
        byte[] data = new byte[positions.Length * 4];
        Buffer.BlockCopy(positions, 0, data, 0, data.Length);

        gl.BindBuffer(GL_ARRAY_BUFFER, _axisVbo);
        BufferData(gl, data, data.Length);

        gl.BindVertexArray(_axisVao);
        gl.BindBuffer(GL_ARRAY_BUFFER, _axisVbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, 24, 0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, 24, 12);
        gl.BindVertexArray(0);
        gl.BindBuffer(GL_ARRAY_BUFFER, 0);
    }

    private static void BufferData(GlInterface gl, byte[] data, int length)
    {
        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { gl.BufferData(GL_ARRAY_BUFFER, new IntPtr(length), handle.AddrOfPinnedObject(), GL_STATIC_DRAW); }
        finally { handle.Free(); }
    }

    private static int MakeTexture(GlInterface gl, RingTexture tex)
    {
        if (tex.Width <= 0 || tex.Height <= 0)
        {
            throw new ArgumentException($"Invalid texture dimensions: {tex.Width}x{tex.Height}");
        }

        if ((long)tex.Width * tex.Height * 4 != tex.Pixels.Length)
        {
            throw new InvalidOperationException(
                $"Pixel buffer size {tex.Pixels.Length} does not match {tex.Width}x{tex.Height}");
        }

        int id = gl.GenTexture();
        gl.BindTexture(GL_TEXTURE_2D, id);
        GCHandle handle = GCHandle.Alloc(tex.Pixels, GCHandleType.Pinned);
        try
        {
            gl.TexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, tex.Width, tex.Height, 0,
                GL_RGBA, GL_UNSIGNED_BYTE, handle.AddrOfPinnedObject());
        }
        catch { gl.DeleteTexture(id); throw; }
        finally { handle.Free(); }

        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, Gl.Repeat);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, Gl.Repeat);
        gl.BindTexture(GL_TEXTURE_2D, 0);
        return id;
    }

    private static int MakeSolidTexture(GlInterface gl, uint rgba)
    {
        int id = gl.GenTexture();
        gl.BindTexture(GL_TEXTURE_2D, id);
        byte[] bytes = BitConverter.GetBytes(rgba);
        GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            gl.TexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, 1, 1, 0, GL_RGBA, GL_UNSIGNED_BYTE,
                handle.AddrOfPinnedObject());
        }
        finally { handle.Free(); }
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
        gl.BindTexture(GL_TEXTURE_2D, 0);
        return id;
    }

    /// <summary>
    /// System.Numerics matrices are row-major (D3D row-vector convention). Uploading
    /// them untransposed makes GLSL read the transpose, so the column-vector product
    /// in the shader equals the row-vector product used here.
    /// Uses pre-allocated scratch buffers to avoid per-frame heap allocation.
    /// </summary>
    private void UniformMatrix(GlInterface gl, int location, Matrix4x4 m)
    {
        if (s_uniformMatrix4fv is null)
        {
            return;
        }

        _matScratch[0] = m.M11; _matScratch[1] = m.M12; _matScratch[2] = m.M13; _matScratch[3] = m.M14;
        _matScratch[4] = m.M21; _matScratch[5] = m.M22; _matScratch[6] = m.M23; _matScratch[7] = m.M24;
        _matScratch[8] = m.M31; _matScratch[9] = m.M32; _matScratch[10] = m.M33; _matScratch[11] = m.M34;
        _matScratch[12] = m.M41; _matScratch[13] = m.M42; _matScratch[14] = m.M43; _matScratch[15] = m.M44;

        GCHandle handle = GCHandle.Alloc(_matScratch, GCHandleType.Pinned);
        try
        {
            _matArgs[0] = location;
            _matArgs[3] = handle.AddrOfPinnedObject();
            _ = s_uniformMatrix4fv.Invoke(gl, _matArgs);
        }
        finally { handle.Free(); }
    }

    private void Uniform4f(GlInterface gl, int location, float x, float y, float z, float w)
    {
        if (s_uniform4f is null)
        {
            return;
        }

        _ = s_uniform4f.Invoke(gl, new object?[] { location, x, y, z, w });
    }

    private static void Check(string? error, string what)
    {
        if (!string.IsNullOrEmpty(error))
        {
            throw new InvalidOperationException($"{what} failed: {error}");
        }
    }

    private const string VertexSource = """
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

    private const string FragmentSource = """
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

    private const string GridVertexSource = """
        layout(location = 0) in vec3 aPos;
        uniform mat4 uMvp;
        void main() { gl_Position = uMvp * vec4(aPos, 1.0); }
        """;

    private const string GridFragmentSource = """
        uniform vec4 uColor;
        out vec4 fragColor;
        void main() { fragColor = uColor; }
        """;

    private const string GridVertexSourceEs = """
        #version 300 es
        precision highp float;
        layout(location = 0) in vec3 aPos;
        uniform mat4 uMvp;
        void main() { gl_Position = uMvp * vec4(aPos, 1.0); }
        """;

    private const string GridFragmentSourceEs = """
        #version 300 es
        precision highp float;
        uniform vec4 uColor;
        out vec4 fragColor;
        void main() { fragColor = uColor; }
        """;

    private const string AxisVertexSource = """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aColor;
        uniform mat4 uMvp;
        out vec3 vColor;
        void main() { vColor = aColor; gl_Position = uMvp * vec4(aPos, 1.0); }
        """;

    private const string AxisFragmentSource = """
        in vec3 vColor;
        out vec4 fragColor;
        void main() { fragColor = vec4(vColor, 1.0); }
        """;

    private const string AxisVertexSourceEs = """
        #version 300 es
        precision highp float;
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aColor;
        uniform mat4 uMvp;
        out vec3 vColor;
        void main() { vColor = aColor; gl_Position = uMvp * vec4(aPos, 1.0); }
        """;

    private const string AxisFragmentSourceEs = """
        #version 300 es
        precision highp float;
        in vec3 vColor;
        out vec4 fragColor;
        void main() { fragColor = vec4(vColor, 1.0); }
        """;
}
