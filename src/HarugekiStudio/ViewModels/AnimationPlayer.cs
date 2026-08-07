using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harugeki.Formats;
using Harugeki.Formats.Math;
using Harugeki.Formats.Skinning;
using System.Diagnostics;
using System.Numerics;

namespace HarugekiStudio.ViewModels;

/// <summary>
/// Plays a clip against a model and produces a posed vertex frame.
///
/// <para>
/// Knows nothing about the viewport: a finished frame goes out through
/// <see cref="FrameReady"/> and the return to rest through
/// <see cref="FrameCleared"/>, so the skinning maths stays testable and the
/// renderer stays replaceable.
/// </para>
/// </summary>
public sealed partial class AnimationPlayer : ObservableObject, IDisposable
{
    private const double TargetFps = 60.0;

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();

    private RingModel? _model;
    private float[]? _positions;
    private float[]? _normals;
    private bool _disposed;

    public AnimationPlayer()
    {
        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(1000.0 / TargetFps), DispatcherPriority.Background, OnTick);
    }

    /// <summary>
    /// The "no clip" entry that heads every model's list.
    ///
    /// <para>
    /// A real <see cref="RingAnimation"/> with no tracks and no frames rather than
    /// a null placeholder, so it can sit in the combo box like any other item and
    /// show its name. Having no tracks is also what makes the dope sheet fall
    /// through to its "No animation selected" message for free.
    /// </para>
    /// </summary>
    public static RingAnimation None { get; } = new() { Name = "None" };

    /// <summary>A posed frame: positions then normals, one entry per draw vertex.</summary>
    public event Action<float[], float[]>? FrameReady;

    /// <summary>The model should return to its rest pose.</summary>
    public event Action? FrameCleared;

    /// <summary>Clips beside the selected model, always led by <see cref="None"/>.</summary>
    public ObservableCollection<RingAnimation> Clips { get; } = [];

    [ObservableProperty]
    public partial RingAnimation? SelectedClip { get; set; }

    public bool IsPlaying { get; private set; }

    public double Time { get; private set; }

    public double Duration => SelectedClip?.Duration ?? 0;

    /// <summary>Whether the clip picker should be usable — true whenever a model is bound.</summary>
    public bool CanSelect => _model is not null && Clips.Count > 0;

    /// <summary>Whether there is something to actually play. False on <see cref="None"/>.</summary>
    public bool CanPlay => _model is not null && SelectedClip is { } clip && !IsNone(clip);

    private static bool IsNone(RingAnimation? clip)
    {
        return ReferenceEquals(clip, None);
    }

    /// <summary>
    /// Points the player at the model on screen and collects the clips beside it.
    ///
    /// <para>
    /// The model being viewed is the model to animate. That sounds obvious, but a
    /// container can hold more than one: every alt costume stores its detachable
    /// hat, ear or hair band as a second model beside the character. Searching the
    /// siblings for a model to animate finds the wrong one of the pair — the
    /// character's clips get skinned onto a bone-less accessory, or the accessory
    /// gets shown with a vertex buffer sized for the character's meshes.
    /// </para>
    /// </summary>
    public void Bind(RingModel model, RingNode? node)
    {
        ArgumentNullException.ThrowIfNull(model);

        Halt();
        Clips.Clear();
        _model = model;

        // "None" leads every model's list, whether or not the container holds any
        // clips, so there is always a way back to the unposed model.
        Clips.Add(None);

        foreach (RingNode? sibling in node?.Parent?.Children ?? [])
        {
            if (sibling is null || !RingAnimation.LooksLikeAnimation(sibling.Span))
            {
                continue;
            }

            try
            {
                Clips.Add(RingAnimation.Parse(sibling.Span, $"anim{sibling.Index:00}"));
            }
            catch (ArgumentException)
            {
                // A sibling that only looks like a clip. Skip it.
            }
        }

        // Start unposed: selecting a model shows it as authored until a clip is picked.
        SelectedClip = None;
        NotifyAvailability();
    }

    /// <summary>Detaches from any model and returns the view to the rest pose.</summary>
    public void Unbind()
    {
        Halt();
        Clips.Clear();
        SelectedClip = null;
        _model = null;
        ShowRestPose();
        NotifyAvailability();
    }

    [RelayCommand]
    public void Play()
    {
        if (_model is null || SelectedClip is not { } clip || IsNone(clip))
        {
            return;
        }

        IsPlaying = true;
        _clock.Restart();
        _timer.Start();
        OnPropertyChanged(nameof(IsPlaying));
    }

    /// <summary>
    /// Stops playback and rewinds to the clip's first frame.
    ///
    /// <para>
    /// Deliberately not a return to the rest pose. Stopping parks the model at the
    /// start of the clip you were watching, which is what you want to look at
    /// next; dropping to rest also forced a full geometry rebuild and re-framed
    /// the camera, throwing away wherever you had orbited to. Choosing
    /// <see cref="None"/> is how you ask for the unposed model.
    /// </para>
    /// </summary>
    [RelayCommand]
    public void Stop()
    {
        Halt();

        if (_model is not null && SelectedClip is { } clip && !IsNone(clip))
        {
            Pose(_model, clip, 0);
        }

        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(Time));
    }

    /// <summary>Stops the clock without touching the posed frame.</summary>
    private void Halt()
    {
        IsPlaying = false;
        _clock.Stop();
        _timer.Stop();
        Time = 0;
    }

    private void ShowRestPose()
    {
        _positions = null;
        _normals = null;
        FrameCleared?.Invoke();
    }

    partial void OnSelectedClipChanged(RingAnimation? value)
    {
        Halt();

        if (_model is null || value is null || IsNone(value))
        {
            ShowRestPose();
        }
        else
        {
            Pose(_model, value, 0);
        }

        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(Time));
        OnPropertyChanged(nameof(Duration));
        NotifyAvailability();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!IsPlaying || SelectedClip is null || _model is null)
        {
            return;
        }

        double duration = SelectedClip.Duration;
        Time = duration > 0 ? _clock.Elapsed.TotalSeconds % duration : 0;
        OnPropertyChanged(nameof(Time));
        Pose(_model, SelectedClip, Time);
    }

    /// <summary>
    /// Evaluates <paramref name="clip"/> at <paramref name="seconds"/> and skins
    /// every mesh into the frame buffers.
    /// </summary>
    private void Pose(RingModel model, RingAnimation clip, double seconds)
    {
        // A bone-less model is still animatable: the detachable accessories that
        // ship beside the alt costumes are a single rigid mesh driven by a track
        // of their own name, so only a model with nothing to draw is a no-op.
        if (model.Meshes.Count == 0)
        {
            return;
        }

        Dictionary<string, Matrix4x4> world = SampleTracks(clip, (float)(seconds * RingAnimation.Fps));
        Matrix4x4[] palette = BuildPalette(model, world);

        int total = model.Meshes.Sum(m => m.VertexCount * 3);
        if (_positions is null || _normals is null || _positions.Length != total)
        {
            _positions = new float[total];
            _normals = new float[total];
        }

        int at = 0;
        foreach (RingMesh mesh in model.Meshes)
        {
            SkinBinding? binding = SkinBinding.Build(model, mesh);
            if (binding is null)
            {
                // Rigid mesh. Its own track is an absolute world transform, so use
                // that whenever the clip carries one.
                Matrix4x4 placement = world.TryGetValue(mesh.Name, out Matrix4x4 own)
                    ? own
                    : RigidFallback(model, mesh, palette);

                SkinRigid(mesh, placement, _positions, _normals, at);
            }
            else
            {
                Skin(mesh, binding, palette, _positions, _normals, at);
            }

            at += mesh.VertexCount * 3;
        }

        FrameReady?.Invoke(_positions, _normals);
    }

    /// <summary>Interpolates every track to the given frame time.</summary>
    private static Dictionary<string, Matrix4x4> SampleTracks(RingAnimation clip, float frame)
    {
        Dictionary<string, Matrix4x4> world = new(StringComparer.OrdinalIgnoreCase);

        foreach (RingAnimation.Track track in clip.Tracks)
        {
            if (track.Times.Length == 0)
            {
                continue;
            }

            int k0 = FindKeyframe(track.Times, frame);
            int k1 = Math.Min(k0 + 1, track.Times.Length - 1);
            float t0 = track.Times[k0];
            float t1 = track.Times[k1];
            float blend = Math.Clamp(t1 > t0 ? (frame - t0) / (t1 - t0) : 0f, 0f, 1f);

            world[track.Name] = Transforms.Blend(
                Transforms.Mirrored(track.Matrices[k0]),
                Transforms.Mirrored(track.Matrices[k1]),
                blend);
        }

        return world;
    }

    /// <summary>
    /// The per-bone skinning matrices.
    ///
    /// <para>
    /// Keys are absolute world transforms, not parent-relative, so there is no
    /// chain accumulation: a bone's matrix is its own inverse bind followed by its
    /// own animated world transform. A bone with no track keeps its bind pose,
    /// which falls out of this as the identity.
    /// </para>
    /// </summary>
    private static Matrix4x4[] BuildPalette(RingModel model, Dictionary<string, Matrix4x4> world)
    {
        Matrix4x4[] palette = new Matrix4x4[model.Bones.Count];
        for (int i = 0; i < palette.Length; i++)
        {
            RingBone bone = model.Bones[i];
            Matrix4x4 bind = Transforms.ToMatrix(bone.Bind);
            Matrix4x4 animated = world.TryGetValue(bone.Name, out Matrix4x4 track) ? track : bind;
            palette[i] = Transforms.InvertOrIdentity(bind) * animated;
        }

        return palette;
    }

    /// <summary>
    /// Transforms every draw vertex by its weighted blend of bone matrices. A
    /// vertex nothing weights keeps the rest position the draw stream already holds.
    /// </summary>
    private static void Skin(
        RingMesh mesh, SkinBinding binding, Matrix4x4[] palette, float[] positions, float[] normals, int at)
    {
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            int to = at + (i * 3);
            int skinVertex = mesh.VertexIds[i];

            if (!binding.TryGetInfluences(skinVertex, out ReadOnlySpan<int> joints, out ReadOnlySpan<float> weights))
            {
                CopyRest(mesh, i, positions, normals, to);
                continue;
            }

            Vector3 restPosition = Read(mesh.SkinPositions, skinVertex);
            Vector3 restNormal = Read(mesh.SkinNormals, skinVertex);
            Vector3 position = Vector3.Zero;
            Vector3 normal = Vector3.Zero;

            for (int k = 0; k < SkinBinding.MaxInfluences; k++)
            {
                float weight = weights[k];
                int joint = joints[k];
                if (weight <= 0f || joint < 0 || joint >= palette.Length)
                {
                    continue;
                }

                position += Vector3.Transform(restPosition, palette[joint]) * weight;
                normal += Vector3.TransformNormal(restNormal, palette[joint]) * weight;
            }

            Write(positions, to, position);
            Write(normals, to, normal);
        }
    }

    /// <summary>Places an unweighted mesh with a single transform.</summary>
    private static void SkinRigid(
        RingMesh mesh, Matrix4x4 placement, float[] positions, float[] normals, int at)
    {
        int skinVertexCount = mesh.SkinPositions.Length / 3;
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            int to = at + (i * 3);
            int skinVertex = mesh.VertexIds[i];

            if (skinVertex < 0 || skinVertex >= skinVertexCount)
            {
                CopyRest(mesh, i, positions, normals, to);
                continue;
            }

            Write(positions, to, Vector3.Transform(Read(mesh.SkinPositions, skinVertex), placement));
            Write(normals, to, Vector3.TransformNormal(Read(mesh.SkinNormals, skinVertex), placement));
        }
    }

    /// <summary>
    /// Transform for a rigid, unweighted mesh that the clip does not name in any
    /// track — an alt costume's hat or ear in one of the borrowed body clips.
    ///
    /// <para>
    /// Which answer is right depends on the space the mesh was authored in, and
    /// the rest positions say which. A mesh whose bounds enclose the origin
    /// (kk00/kk01, sister01's hat) is modelled in its own local space: only its
    /// own track can place it, and multiplying it by some bone's matrix throws it
    /// across the scene, so it stays where the static view draws it. A mesh that
    /// already sits out at the skeleton (hat_kyon01 up at head height) shares the
    /// bind-pose space and can ride the bone it is resting on.
    /// </para>
    /// </summary>
    private static Matrix4x4 RigidFallback(RingModel model, RingMesh mesh, Matrix4x4[] palette)
    {
        int count = mesh.SkinPositions.Length / 3;
        if (count == 0 || model.Bones.Count == 0)
        {
            return Matrix4x4.Identity;
        }

        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        Vector3 sum = Vector3.Zero;

        for (int i = 0; i < count; i++)
        {
            Vector3 p = Read(mesh.SkinPositions, i);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            sum += p;
        }

        bool enclosesOrigin = min.X <= 0f && max.X >= 0f
            && min.Y <= 0f && max.Y >= 0f
            && min.Z <= 0f && max.Z >= 0f;

        if (enclosesOrigin)
        {
            return Matrix4x4.Identity;
        }

        Vector3 centre = sum / count;
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int b = 0; b < model.Bones.Count; b++)
        {
            float[] bind = model.Bones[b].Bind;
            float d = Vector3.DistanceSquared(centre, new Vector3(bind[12], bind[13], bind[14]));
            if (d < bestDistance)
            {
                bestDistance = d;
                best = b;
            }
        }

        return best >= 0 ? palette[best] : Matrix4x4.Identity;
    }

    /// <summary>The last key at or before <paramref name="frame"/>.</summary>
    private static int FindKeyframe(float[] times, float frame)
    {
        for (int i = times.Length - 1; i >= 0; i--)
        {
            if (times[i] <= frame)
            {
                return i;
            }
        }

        return 0;
    }

    private static void CopyRest(RingMesh mesh, int drawVertex, float[] positions, float[] normals, int to)
    {
        int from = drawVertex * 3;
        mesh.Positions.AsSpan(from, 3).CopyTo(positions.AsSpan(to));
        mesh.Normals.AsSpan(from, 3).CopyTo(normals.AsSpan(to));
    }

    private static Vector3 Read(float[] source, int vertex)
    {
        return new Vector3(source[vertex * 3], source[(vertex * 3) + 1], source[(vertex * 3) + 2]);
    }

    private static void Write(float[] destination, int at, in Vector3 value)
    {
        destination[at] = value.X;
        destination[at + 1] = value.Y;
        destination[at + 2] = value.Z;
    }

    private void NotifyAvailability()
    {
        OnPropertyChanged(nameof(CanSelect));
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(Duration));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _clock.Stop();
    }
}
