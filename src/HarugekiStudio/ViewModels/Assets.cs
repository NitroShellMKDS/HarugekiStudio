using Harugeki.Formats;

namespace HarugekiStudio.ViewModels;

public sealed record TextSegment(string Text, bool IsHighlight);

/// <summary>Pairs a decoded asset with the container node it came from.</summary>
public sealed record ModelAsset(RingNode Node, RingModel Model);
public sealed record MeshAsset(RingNode Node, RingModel Model, RingMesh Mesh);

/// <summary>
/// A texture and the route back to its bytes. An archive-entry texture is replaced
/// in-place; an embedded texture is spliced at a known offset so the model is untouched.
/// </summary>
public sealed record TextureAsset(
    RingNode Node, RingTexture Texture, RingModel? Owner = null, int IndexInOwner = 0)
{
    public void Replace(byte[] rgbaPixels)
    {
        if (Node.Archive == null)
        {
            throw new InvalidOperationException("Texture's archive has been unloaded");
        }

        RingTexture replacement = new()
        {
            Name = Texture.Name,
            Width = Texture.Width,
            Height = Texture.Height,
            Pixels = rgbaPixels,
            HeaderTail = Texture.HeaderTail,
        };
        byte[] encoded = replacement.Build();

        if (Owner is null)
        {
            byte[] payload = Node.GetPayload();
            if (encoded.Length > payload.Length)
            {
                throw new InvalidDataException(
                    $"Encoded texture ({encoded.Length} bytes) exceeds slot size ({payload.Length} bytes)");
            }

            encoded.CopyTo(payload, 0);
            Node.Replace(payload);
        }
        else
        {
            if (IndexInOwner < 0 || IndexInOwner >= Owner.TextureSpans.Count)
            {
                throw new InvalidOperationException(
                    $"Texture index {IndexInOwner} out of range for model");
            }

            (int offset, int length) = Owner.TextureSpans[IndexInOwner];
            if (encoded.Length > length)
            {
                throw new InvalidDataException(
                    $"Encoded embedded texture ({encoded.Length} bytes) exceeds slot size ({length} bytes)");
            }

            byte[] payload = Node.GetPayload();
            if (payload.Length < offset + encoded.Length)
            {
                throw new InvalidOperationException("Model blob is corrupted or truncated");
            }

            encoded.CopyTo(payload, offset);
            Node.Replace(payload);
        }

        Texture.Pixels.AsSpan().Clear();
        rgbaPixels.CopyTo(Texture.Pixels, 0);
    }
}
