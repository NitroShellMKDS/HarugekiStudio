using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Harugeki.Formats;
using System.Numerics;
using System.Runtime.InteropServices;
using static Avalonia.OpenGL.GlConsts;

namespace HarugekiStudio.Rendering;

public enum ShadingMode
{
    Textured,
    Shaded,
    Wireframe,
    Normals,
    Uv,
}

/// <summary>Constants Avalonia's <see cref="GlConsts"/> does not define.</summary>
internal static class Gl
{
    public const int Lines = 0x0001;
    public const int Repeat = 0x2901;
    public const int DynamicDraw = 0x88E8;
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

    // ---- camera ----------------------------------------------------------
    private float _yaw = 0.6f;
    private float _pitch = 0.25f;
    private float _distance = 300f;
    private float _targetDistance = 300f;
    private bool _zoomAnimating;
    private Vector3 _target = new(0, 80, 0);

    // ---- invalidation ----------------------------------------------------

    /// <summary>The model changed: rebuild and re-upload everything.</summary>
    private bool _geometryDirty = true;

    /// <summary>
    /// A new animation pose arrived and has not been uploaded yet.
    ///
    /// <para>
    /// Without this the render loop re-packed and re-uploaded the entire mesh on
    /// every frame for as long as an animation was selected — including while
    /// paused, so merely orbiting the camera re-sent the whole vertex buffer.
    /// </para>
    /// </summary>
    private bool _poseDirty;

    // ---- gl objects ------------------------------------------------------
    private GlProgram? _model;
    private GlProgram? _flat;
    private GlProgram? _vertexColor;
    private GlUniforms? _uniforms;

    private int _vao, _lineVao, _vbo, _lineVbo, _white;
    private int _uMvp, _uView, _uMode, _uHasTex, _uTex;
    private int _gridVao, _gridVbo, _gridMvp, _gridVertexCount;
    private int _gridR, _gridG, _gridB;
    private int _axisVao, _axisVbo, _axisMvp, _axisVertexCount;

    private readonly List<int> _textures = [];
    private MeshBuffer? _buffer;
    private bool _ready;

    /// <summary>
    /// The model the camera was last framed for. Re-uploading geometry for the
    /// same model — which happens on a shading change or a return to rest —
    /// must not re-frame the camera and lose the user's viewpoint.
    /// </summary>
    private RingModel? _framedFor;

    private float[]? _posePositions;
    private float[]? _poseNormals;

    /// <summary>
    /// Clear and grid colours for the live theme. GL cannot read a resource
    /// dictionary, so they are resolved from this control's resources whenever
    /// the variant changes and cached as floats for the render loop.
    /// </summary>
    private (float R, float G, float B) _background = (0.1f, 0.1f, 0.1f);
    private (float R, float G, float B) _grid = (0.5f, 0.5f, 0.5f);

    static GlViewport()
    {
        _ = ShadingProperty.Changed.AddClassHandler<GlViewport>((v, _) => v.RequestNextFrameRendering());
        _ = ModelProperty.Changed.AddClassHandler<GlViewport>((v, _) => v.InvalidateGeometry());
    }

    public GlViewport()
    {
        Focusable = true;
        ClipToBounds = true;

        // Notified as an event rather than an override: StyledElement exposes
        // ActualThemeVariantChanged, not a virtual On… hook.
        ActualThemeVariantChanged += OnThemeChanged;
    }

    public RingModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public ShadingMode Shading
    {
        get => GetValue(ShadingProperty);
        set => SetValue(ShadingProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ReadThemeColours();
        if (_ready)
        {
            InvalidateGeometry();
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ReadThemeColours();
        RequestNextFrameRendering();
    }

    /// <summary>
    /// Pulls the viewport's two colours out of the resource dictionary for the
    /// live theme variant, so the 3D view sits on the same surface as the panels
    /// around it in both light and dark.
    /// </summary>
    private void ReadThemeColours()
    {
        _background = Resolve("SurfaceWell", (0.11f, 0.11f, 0.11f));
        _grid = Resolve("ViewportGrid", (0.5f, 0.5f, 0.5f));

        (float, float, float) Resolve(string key, (float, float, float) fallback)
        {
            return this.TryFindResource(key, ActualThemeVariant, out object? value)
                && value is ISolidColorBrush brush
                    ? (brush.Color.R / 255f, brush.Color.G / 255f, brush.Color.B / 255f)
                    : fallback;
        }
    }

    // ---- camera controls -------------------------------------------------
    public void Orbit(double dx, double dy)
    {
        _yaw -= (float)dx * 0.01f;
        _pitch = Math.Clamp(_pitch + ((float)dy * 0.01f), -1.5f, 1.5f);
        RequestNextFrameRendering();
    }

    public void Pan(double dx, double dy)
    {
        float scale = _distance * 0.0015f;
        Vector3 right = new(MathF.Cos(_yaw), 0, -MathF.Sin(_yaw));
        _target += (right * (float)-dx * scale) + (Vector3.UnitY * (float)dy * scale);
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
        foreach (RingMesh mesh in model.Meshes)
        {
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                float y = mesh.Positions[(i * 3) + 1];
                minY = MathF.Min(minY, y);
                maxY = MathF.Max(maxY, y);
                extent = MathF.Max(extent, MathF.Abs(mesh.Positions[i * 3]));
                extent = MathF.Max(extent, MathF.Abs(mesh.Positions[(i * 3) + 2]));
            }
        }

        _target = new Vector3(0, (minY + maxY) * 0.5f, 0);
        _distance = _targetDistance = MathF.Max(maxY - minY, extent * 2f) * 1.6f;
        _yaw = 0.6f;
        _pitch = 0.15f;
        RequestNextFrameRendering();
    }

    // ---- animation hand-off ----------------------------------------------

    /// <summary>Supplies a posed frame. Both arrays hold one entry per draw vertex.</summary>
    public void SetAnimatedFrame(float[]? positions, float[]? normals)
    {
        _posePositions = positions;
        _poseNormals = normals;
        _poseDirty = positions is not null && normals is not null;
        RequestNextFrameRendering();
    }

    /// <summary>
    /// Drops the current pose and returns the model to its rest position.
    ///
    /// <para>
    /// Marking the geometry dirty is the point: the posed vertices were written
    /// over the same buffer the rest pose lives in, so clearing the arrays alone
    /// used to leave the model frozen in whatever pose it stopped on.
    /// </para>
    /// </summary>
    public void ClearAnimatedFrame()
    {
        if (_posePositions is null && _poseNormals is null)
        {
            return;
        }

        _posePositions = null;
        _poseNormals = null;

        // Re-pack at rest rather than marking the geometry dirty: a full rebuild
        // would also re-upload every texture and re-frame the camera, throwing
        // away wherever the user had orbited to.
        _poseDirty = true;
        RequestNextFrameRendering();
    }

    private void InvalidateGeometry()
    {
        _geometryDirty = true;
        RequestNextFrameRendering();
    }

    // ---- gl lifecycle ----------------------------------------------------
    protected override void OnOpenGlInit(GlInterface gl)
    {
        _uniforms = GlUniforms.Create(gl);
        string header = Shaders.Header(GlVersion);

        _model = GlProgram.Build(gl, header, Shaders.ModelVertex, Shaders.ModelFragment, "model");
        _uMvp = _model.Uniform(gl, "uMvp");
        _uView = _model.Uniform(gl, "uView");
        _uMode = _model.Uniform(gl, "uMode");
        _uHasTex = _model.Uniform(gl, "uHasTex");
        _uTex = _model.Uniform(gl, "uTex");

        _flat = GlProgram.Build(gl, header, Shaders.FlatVertex, Shaders.FlatFragment, "grid");
        _gridMvp = _flat.Uniform(gl, "uMvp");
        _gridR = _flat.Uniform(gl, "uR");
        _gridG = _flat.Uniform(gl, "uG");
        _gridB = _flat.Uniform(gl, "uB");

        _vertexColor = GlProgram.Build(
            gl, header, Shaders.VertexColorVertex, Shaders.VertexColorFragment, "axis");
        _axisMvp = _vertexColor.Uniform(gl, "uMvp");

        _vao = gl.GenVertexArray();
        _lineVao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        _lineVbo = gl.GenBuffer();
        _white = MakeSolidTexture(gl, 0xFFFFFFFF);

        _gridVao = gl.GenVertexArray();
        _gridVbo = gl.GenBuffer();
        BuildGrid(gl);

        _axisVao = gl.GenVertexArray();
        _axisVbo = gl.GenBuffer();
        BuildAxes(gl);

        _ready = true;
        InvalidateGeometry();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (!_ready)
        {
            return;
        }

        ReleaseTextures(gl);

        gl.DeleteBuffer(_vbo);
        gl.DeleteBuffer(_lineVbo);
        gl.DeleteBuffer(_gridVbo);
        gl.DeleteBuffer(_axisVbo);
        gl.DeleteVertexArray(_vao);
        gl.DeleteVertexArray(_lineVao);
        gl.DeleteVertexArray(_gridVao);
        gl.DeleteVertexArray(_axisVao);
        gl.DeleteTexture(_white);

        _model?.Delete(gl);
        _flat?.Delete(gl);
        _vertexColor?.Delete(gl);
        _model = _flat = _vertexColor = null;

        _uniforms?.Dispose();
        _uniforms = null;

        _vao = _lineVao = _vbo = _lineVbo = _white = 0;
        _uMvp = _uView = _uMode = _uHasTex = _uTex = 0;
        _gridVao = _gridVbo = _gridMvp = _gridVertexCount = 0;
        _gridR = _gridG = _gridB = 0;
        _axisVao = _axisVbo = _axisMvp = _axisVertexCount = 0;

        _buffer = null;
        _posePositions = null;
        _poseNormals = null;
        _poseDirty = false;
        _geometryDirty = true;
        _ready = false;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_uniforms is null)
        {
            return;
        }

        AnimateZoom();

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)(Bounds.Width * scaling));
        int h = Math.Max(1, (int)(Bounds.Height * scaling));

        gl.Viewport(0, 0, w, h);
        gl.ClearColor(_background.R, _background.G, _background.B, 1f);
        gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
        gl.Enable(GL_DEPTH_TEST);
        gl.DepthFunc(GL_LESS);
        gl.DepthMask(1);

        // The game's meshes are authored two-sided; culling would punch holes.
        gl.Disable(GL_CULL_FACE);

        if (_geometryDirty)
        {
            UploadGeometry(gl);
            _geometryDirty = false;
        }
        else if (_poseDirty)
        {
            UploadPose(gl);
            _poseDirty = false;
        }

        Matrix4x4 view = Matrix4x4.CreateLookAt(EyePosition(), _target, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            0.9f, w / (float)h, MathF.Max(0.05f, _distance * 0.002f), (_distance * 20f) + 5000f);
        Matrix4x4 mvp = view * projection;

        DrawGrid(gl, mvp);
        DrawAxes(gl, mvp);
        DrawModel(gl, mvp, view);

        gl.BindVertexArray(0);
        gl.UseProgram(0);
    }

    private void AnimateZoom()
    {
        if (!_zoomAnimating)
        {
            return;
        }

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

    private void DrawGrid(GlInterface gl, in Matrix4x4 mvp)
    {
        if (_gridVertexCount == 0 || _flat is null || _uniforms is null)
        {
            return;
        }

        // Drawn depth-read but not depth-written, so the model always wins.
        gl.DepthMask(0);
        _flat.Use(gl);
        _uniforms.Matrix(_gridMvp, mvp);
        gl.Uniform1f(_gridR, _grid.R);
        gl.Uniform1f(_gridG, _grid.G);
        gl.Uniform1f(_gridB, _grid.B);
        gl.BindVertexArray(_gridVao);
        gl.DrawArrays(Gl.Lines, 0, _gridVertexCount);
        gl.DepthMask(1);
    }

    private void DrawAxes(GlInterface gl, in Matrix4x4 mvp)
    {
        if (_axisVertexCount == 0 || _vertexColor is null || _uniforms is null)
        {
            return;
        }

        _vertexColor.Use(gl);
        _uniforms.Matrix(_axisMvp, mvp);
        gl.BindVertexArray(_axisVao);
        gl.DrawArrays(Gl.Lines, 0, _axisVertexCount);
    }

    private void DrawModel(GlInterface gl, in Matrix4x4 mvp, in Matrix4x4 view)
    {
        if (_buffer is null || _buffer.VertexCount == 0 || _model is null || _uniforms is null)
        {
            return;
        }

        _model.Use(gl);
        _uniforms.Matrix(_uMvp, mvp);
        _uniforms.Matrix(_uView, view);
        gl.Uniform1i(_uTex, 0);

        if (Shading == ShadingMode.Wireframe)
        {
            // GL_LINES instead of glPolygonMode: the polygon-mode entry point does
            // not exist in GLES and would silently do nothing under ANGLE.
            gl.Uniform1i(_uMode, (int)ShadingMode.Wireframe);
            gl.Uniform1f(_uHasTex, 0f);
            gl.BindVertexArray(_lineVao);
            gl.DrawArrays(Gl.Lines, 0, _buffer.LineVertexCount);
            return;
        }

        gl.BindVertexArray(_vao);
        gl.Uniform1i(_uMode, (int)Shading);
        foreach ((int first, int count, int texture) in _buffer.Batches)
        {
            bool textured = Shading == ShadingMode.Textured && texture != 0;
            gl.ActiveTexture(GL_TEXTURE0);
            gl.BindTexture(GL_TEXTURE_2D, textured ? texture : _white);
            gl.Uniform1f(_uHasTex, textured ? 1f : 0f);
            gl.DrawArrays(GL_TRIANGLES, first, count);
        }
    }

    private Vector3 EyePosition()
    {
        float cp = MathF.Cos(_pitch);
        return _target + (new Vector3(
            MathF.Sin(_yaw) * cp, MathF.Sin(_pitch), MathF.Cos(_yaw) * cp) * _distance);
    }

    // ---- geometry upload -------------------------------------------------

    /// <summary>Rebuilds textures and vertex buffers for the current model.</summary>
    private void UploadGeometry(GlInterface gl)
    {
        ReleaseTextures(gl);
        _buffer = null;

        RingModel? model = Model;
        if (model is null || model.Meshes.Count == 0)
        {
            return;
        }

        foreach (RingTexture texture in model.Textures)
        {
            _textures.Add(MakeTexture(gl, texture));
        }

        _buffer = MeshBuffer.Build(model, _textures);

        // A pose that outlived its model would index past the new vertex arrays.
        if (_poseDirty && _posePositions?.Length == _buffer.VertexCount * 3)
        {
            _buffer.Pack(model, _posePositions, _poseNormals);
            _poseDirty = false;
        }
        else
        {
            _posePositions = null;
            _poseNormals = null;
            _poseDirty = false;
        }

        UploadBuffers(gl, GL_STATIC_DRAW);

        gl.BindVertexArray(_vao);
        BindAttribs(gl, _vbo);
        gl.BindVertexArray(_lineVao);
        BindAttribs(gl, _lineVbo);
        gl.BindVertexArray(0);

        if (!ReferenceEquals(_framedFor, model))
        {
            _framedFor = model;
            ResetCamera();
        }
    }

    /// <summary>Re-packs the current pose into the existing buffers.</summary>
    private void UploadPose(GlInterface gl)
    {
        RingModel? model = Model;
        if (_buffer is null || model is null)
        {
            return;
        }

        // A pose that outlived its model would index past the vertex arrays.
        if (_posePositions is not null && _posePositions.Length != _buffer.VertexCount * 3)
        {
            return;
        }

        // Null positions mean "rest pose"; Pack falls back to the mesh's own arrays.
        _buffer.Pack(model, _posePositions, _poseNormals);
        UploadBuffers(gl, Gl.DynamicDraw);
    }

    private void UploadBuffers(GlInterface gl, int usage)
    {
        if (_buffer is null)
        {
            return;
        }

        gl.BindBuffer(GL_ARRAY_BUFFER, _vbo);
        BufferData(gl, _buffer.Vertices, _buffer.VertexCount * MeshBuffer.Stride, usage);
        gl.BindBuffer(GL_ARRAY_BUFFER, _lineVbo);
        BufferData(gl, _buffer.Lines, _buffer.LineVertexCount * MeshBuffer.Stride, usage);
        gl.BindBuffer(GL_ARRAY_BUFFER, 0);
    }

    private static void BindAttribs(GlInterface gl, int buffer)
    {
        gl.BindBuffer(GL_ARRAY_BUFFER, buffer);
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, MeshBuffer.Stride, 0);
        gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, MeshBuffer.Stride, 12);
        gl.VertexAttribPointer(2, 2, GL_FLOAT, 0, MeshBuffer.Stride, 24);
        gl.VertexAttribPointer(3, 4, GL_UNSIGNED_BYTE, 1, MeshBuffer.Stride, 32);
        for (int i = 0; i < 4; i++)
        {
            gl.EnableVertexAttribArray(i);
        }
    }

    private void ReleaseTextures(GlInterface gl)
    {
        foreach (int texture in _textures)
        {
            gl.DeleteTexture(texture);
        }

        _textures.Clear();
    }

    private void BuildGrid(GlInterface gl)
    {
        const int range = 2000;
        const int step = 20;

        List<float> positions = new(((range / step * 2) + 1) * 12);
        for (int x = -range; x <= range; x += step)
        {
            positions.AddRange([x, 0, -range, x, 0, range]);
        }

        for (int z = -range; z <= range; z += step)
        {
            positions.AddRange([-range, 0, z, range, 0, z]);
        }

        _gridVertexCount = positions.Count / 3;
        UploadStaticLines(gl, _gridVao, _gridVbo, [.. positions], stride: 12, colored: false);
    }

    private void BuildAxes(GlInterface gl)
    {
        const float length = 100f;

        // Position then colour per vertex: X red, Y green, Z blue.
        float[] positions =
        [
            0f, 0f, 0f, 1f, 0f, 0f, length, 0f, 0f, 1f, 0f, 0f,
            0f, 0f, 0f, 0f, 1f, 0f, 0f, length, 0f, 0f, 1f, 0f,
            0f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, -length, 0f, 0f, 1f,
        ];

        _axisVertexCount = positions.Length / 6;
        UploadStaticLines(gl, _axisVao, _axisVbo, positions, stride: 24, colored: true);
    }

    private static void UploadStaticLines(
        GlInterface gl, int vao, int vbo, float[] positions, int stride, bool colored)
    {
        byte[] data = new byte[positions.Length * sizeof(float)];
        Buffer.BlockCopy(positions, 0, data, 0, data.Length);

        gl.BindBuffer(GL_ARRAY_BUFFER, vbo);
        BufferData(gl, data, data.Length, GL_STATIC_DRAW);

        gl.BindVertexArray(vao);
        gl.BindBuffer(GL_ARRAY_BUFFER, vbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, stride, 0);
        if (colored)
        {
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, stride, 12);
        }

        gl.BindVertexArray(0);
        gl.BindBuffer(GL_ARRAY_BUFFER, 0);
    }

    /// <summary>Uploads <paramref name="length"/> bytes, pinning for the call.</summary>
    private static void BufferData(GlInterface gl, byte[] data, int length, int usage)
    {
        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            gl.BufferData(GL_ARRAY_BUFFER, new IntPtr(length), handle.AddrOfPinnedObject(), usage);
        }
        finally
        {
            handle.Free();
        }
    }

    private static int MakeTexture(GlInterface gl, RingTexture texture)
    {
        if (texture.Width <= 0 || texture.Height <= 0)
        {
            throw new ArgumentException($"Invalid texture dimensions: {texture.Width}x{texture.Height}");
        }

        if ((long)texture.Width * texture.Height * 4 != texture.Pixels.Length)
        {
            throw new InvalidOperationException(
                $"Pixel buffer size {texture.Pixels.Length} does not match {texture.Width}x{texture.Height}");
        }

        int id = gl.GenTexture();
        gl.BindTexture(GL_TEXTURE_2D, id);

        GCHandle handle = GCHandle.Alloc(texture.Pixels, GCHandleType.Pinned);
        try
        {
            gl.TexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, texture.Width, texture.Height, 0,
                GL_RGBA, GL_UNSIGNED_BYTE, handle.AddrOfPinnedObject());
        }
        catch
        {
            gl.DeleteTexture(id);
            throw;
        }
        finally
        {
            handle.Free();
        }

        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, Gl.Repeat);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, Gl.Repeat);
        gl.BindTexture(GL_TEXTURE_2D, 0);
        return id;
    }

    /// <summary>A 1x1 texture, bound wherever a material has none.</summary>
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
        finally
        {
            handle.Free();
        }

        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
        gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
        gl.BindTexture(GL_TEXTURE_2D, 0);
        return id;
    }
}
