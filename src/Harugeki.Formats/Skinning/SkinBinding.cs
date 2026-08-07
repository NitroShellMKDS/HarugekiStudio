namespace Harugeki.Formats.Skinning;

/// <summary>
/// The bone influences acting on one mesh, resolved to the four heaviest per
/// vertex and normalised to sum to 1.
///
/// <para>
/// The file stores this the other way round — each bone owns a list of the
/// vertices it pulls on — so getting from there to a per-vertex answer means
/// inverting the relation, keeping the top four, and renormalising. Both the
/// viewport's CPU skinning and the glTF exporter need exactly that, and both
/// used to do it themselves; this is the one implementation they now share.
/// </para>
///
/// <para>
/// Weights are indexed by <b>skin</b> vertex. A draw vertex reaches its skin
/// vertex through <see cref="RingMesh.VertexIds"/>, which is why callers pass
/// that index rather than a draw index.
/// </para>
/// </summary>
public sealed class SkinBinding
{
    /// <summary>Joints per vertex. Four is what glTF and every GPU skinning path assume.</summary>
    public const int MaxInfluences = 4;

    private readonly int[] _joints;
    private readonly float[] _weights;

    private SkinBinding(int skinVertexCount)
    {
        SkinVertexCount = skinVertexCount;
        _joints = new int[skinVertexCount * MaxInfluences];
        _weights = new float[skinVertexCount * MaxInfluences];
    }

    public int SkinVertexCount { get; }

    /// <summary>
    /// Resolves the influences on <paramref name="mesh"/>, or <see langword="null"/>
    /// if no bone weights it at all — a rigid mesh, which the caller must place by
    /// its own transform rather than by skinning.
    /// </summary>
    public static SkinBinding? Build(RingModel model, RingMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(mesh);

        int skinVertexCount = mesh.SkinPositions.Length / 3;
        if (skinVertexCount == 0)
        {
            return null;
        }

        SkinBinding binding = new(skinVertexCount);
        bool any = false;

        // The joint index is the bone's position in the model's bone list, which
        // is what both the glTF skin and the viewport's matrix palette index by.
        for (int jointIndex = 0; jointIndex < model.Bones.Count; jointIndex++)
        {
            RingBone bone = model.Bones[jointIndex];
            if (bone.MeshIndex != mesh.NodeIndex)
            {
                continue;
            }

            for (int k = 0; k < bone.Weights.Length; k++)
            {
                int vertex = bone.WeightVertices[k];
                float weight = bone.Weights[k];

                // Out-of-range and non-positive entries do occur in the shipped
                // data; dropping them here is what keeps every consumer safe.
                if (vertex < 0 || vertex >= skinVertexCount || weight <= 0f)
                {
                    continue;
                }

                binding.Insert(vertex, weight, jointIndex);
                any = true;
            }
        }

        if (!any)
        {
            return null;
        }

        binding.Normalise();
        return binding;
    }

    /// <summary>
    /// The influences on <paramref name="skinVertex"/>, or <see langword="false"/>
    /// when the index is out of range or nothing weights that vertex. The spans
    /// are always <see cref="MaxInfluences"/> long, zero-padded.
    /// </summary>
    public bool TryGetInfluences(int skinVertex, out ReadOnlySpan<int> joints, out ReadOnlySpan<float> weights)
    {
        if (skinVertex < 0 || skinVertex >= SkinVertexCount)
        {
            joints = default;
            weights = default;
            return false;
        }

        int at = skinVertex * MaxInfluences;
        joints = _joints.AsSpan(at, MaxInfluences);
        weights = _weights.AsSpan(at, MaxInfluences);
        return weights[0] > 0f;
    }

    /// <summary>Inserts one influence into a vertex's descending top-four list.</summary>
    private void Insert(int vertex, float weight, int joint)
    {
        int at = vertex * MaxInfluences;
        for (int slot = 0; slot < MaxInfluences; slot++)
        {
            if (weight <= _weights[at + slot])
            {
                continue;
            }

            for (int shift = MaxInfluences - 1; shift > slot; shift--)
            {
                _weights[at + shift] = _weights[at + shift - 1];
                _joints[at + shift] = _joints[at + shift - 1];
            }

            _weights[at + slot] = weight;
            _joints[at + slot] = joint;
            return;
        }
    }

    /// <summary>
    /// Rescales each vertex's weights to sum to 1. Necessary because dropping all
    /// but the top four loses whatever the discarded influences contributed, and
    /// glTF requires normalised weights regardless.
    /// </summary>
    private void Normalise()
    {
        for (int at = 0; at < _weights.Length; at += MaxInfluences)
        {
            float sum = 0f;
            for (int k = 0; k < MaxInfluences; k++)
            {
                sum += _weights[at + k];
            }

            if (sum is 0f or 1f)
            {
                continue;
            }

            for (int k = 0; k < MaxInfluences; k++)
            {
                _weights[at + k] /= sum;
            }
        }
    }
}
