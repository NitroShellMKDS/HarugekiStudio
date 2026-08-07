namespace Harugeki.Formats;

/// <summary>One material record: a name, an optional texture, and a base colour.</summary>
public sealed class RingMaterial
{
    /// <summary>Sentinel the format uses for "no texture".</summary>
    public const uint NoTexture = 0xFFFFFFFF;

    public string Name { get; init; } = string.Empty;

    /// <summary>Index into the model's embedded textures; <see cref="NoTexture"/> when untextured.</summary>
    public uint TextureIndex { get; init; }

    public float[] Color { get; init; } = [1, 1, 1, 1];

    public bool HasTexture => TextureIndex != NoTexture;

    public override string ToString()
    {
        return $"{Name} tex={(HasTexture ? TextureIndex : -1)}";
    }
}
