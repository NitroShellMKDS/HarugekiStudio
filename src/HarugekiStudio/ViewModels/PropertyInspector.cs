using Harugeki.Formats;
using HarugekiStudio.Audio;

namespace HarugekiStudio.ViewModels;

/// <summary>One name/value line in the Properties pane.</summary>
public sealed record PropertyRow(string Name, string Value);

/// <summary>
/// Turns whatever the Outliner has selected into the rows shown beside it.
///
/// <para>
/// Every asset kind used to build its rows in a different way — a local function
/// here, a second copy of that local function there, and direct
/// <c>Properties.Add</c> calls somewhere else. This is the one place a row is
/// made, so they all format alike.
/// </para>
/// </summary>
public static class PropertyInspector
{
    public static IEnumerable<PropertyRow> Describe(object? payload, RingNode? node, AudioInfo? audio = null)
    {
        List<PropertyRow> rows = [];

        switch (payload)
        {
            case ModelAsset model:
                Add("Name", model.Model.Name);
                Add("Bones", model.Model.Bones.Count);
                Add("Meshes", model.Model.Meshes.Count);
                Add("Triangles", model.Model.Meshes.Sum(m => m.TriangleCount));
                Add("Embedded textures", model.Model.Textures.Count);
                AddLocation(model.Node);
                break;

            case MeshAsset mesh:
                Add("Name", mesh.Mesh.Name);
                Add("Triangles", mesh.Mesh.TriangleCount);
                Add("Draw vertices", mesh.Mesh.VertexCount);
                Add("Skin vertices", mesh.Mesh.SkinPositions.Length / 3);
                Add("Materials", string.Join(", ", mesh.Mesh.Materials.Select(m => m.Name)));
                Add("Per-mat tris", string.Join(", ", mesh.Mesh.TriangleCounts));
                AddLocation(mesh.Node);
                break;

            case TextureAsset texture:
                Add("Name", texture.Texture.Name);
                Add("Format", "RGBA8 uncompressed");
                Add("Size", $"{texture.Texture.Width} x {texture.Texture.Height}");
                AddLocation(texture.Node);
                break;

            case AudioAsset clip:
                Add("Format", audio?.Format ?? (AssetTypes.IsOgg(clip.Node.Span) ? "OGG Vorbis" : "WAV"));
                if (audio is { } known)
                {
                    Add("Sample rate", $"{known.SampleRate:N0} Hz");
                    Add("Samples", $"{known.TotalSamples:N0}");
                    Add("Bit depth", $"{known.BitsPerSample}-bit");
                    Add("Channels", known.Channels switch { 1 => "Mono", 2 => "Stereo", _ => known.Channels });
                    Add("Duration", $"{known.Duration:mm\\:ss\\.fff}");
                }

                AddLocation(clip.Node);
                break;

            case RingBone bone:
                Add("Name", bone.Name);
                Add("Node index", bone.NodeIndex);
                Add("Parent node", bone.ParentNodeIndex);
                Add("Weighted verts", bone.Weights.Length);
                AddLocation(node);
                break;

            case RingMaterial material:
                Add("Name", material.Name);
                Add("Texture index", material.HasTexture ? material.TextureIndex : "none");
                Add("Colour", string.Join(", ", material.Color.Select(c => c.ToString("0.###"))));
                AddLocation(node);
                break;

            case RingAnimation animation:
                Add("Name", animation.Name);
                Add("Frames", animation.Frames);
                Add("Duration", $"{animation.Duration:0.00} s");
                Add("Tracks", animation.Tracks.Count);
                Add("Keys/track", animation.Tracks.Count > 0 ? animation.Tracks[0].Times.Length : 0);
                AddLocation(node);
                break;

            case RingArchive archive:
                Add("Archive", Path.GetFileName(archive.Path ?? ""));
                Add("Size", TreeBuilder.Size(archive.Data.Length));
                Add("Root entries", archive.Root.Children.Count);
                break;

            case RingNode slot:
                Add("Slot", slot.PathText);
                if (slot.IsDirectory)
                {
                    Add("Children", slot.Children.Count);
                }

                AddLocation(slot);
                break;

            default:
                break;
        }

        return rows;

        void Add(string name, object? value)
        {
            rows.Add(new PropertyRow(name, value?.ToString() ?? ""));
        }

        void AddLocation(RingNode? at)
        {
            if (at is null)
            {
                return;
            }

            Add("Offset", $"0x{at.Offset:X}");
            Add("Size", TreeBuilder.Size(at.Length));
        }
    }
}
