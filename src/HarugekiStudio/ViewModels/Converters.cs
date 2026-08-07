using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using System.Globalization;

namespace HarugekiStudio.ViewModels;

/// <summary>Resolves a brush from the application's resources by key.</summary>
internal static class PaletteLookup
{
    /// <summary>
    /// Looks <paramref name="key"/> up against the live theme variant, falling
    /// back to a visible grey rather than throwing if it is missing.
    /// </summary>
    public static IBrush Brush(string key)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            return Brushes.Gray;
        }

        ThemeVariant variant = app.ActualThemeVariant;
        return app.TryFindResource(key, variant, out object? found) && found is IBrush brush
            ? brush
            : Brushes.Gray;
    }
}

/// <summary>
/// Maps an Outliner row's asset kind to its colour dot.
///
/// <para>
/// Safe to resolve once per row despite the theme being changeable at runtime:
/// the asset-kind colours in <c>Themes/Palette.axaml</c> are deliberately
/// variant-independent, because a categorical colour has to mean the same thing
/// in light and dark. Anything that *does* vary by theme is bound with
/// <c>DynamicResource</c> or a style class instead of coming through here.
/// </para>
/// </summary>
public sealed class AssetKindBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, string> s_keys = new(StringComparer.Ordinal)
    {
        ["Model"] = "AssetModelBrush",
        ["Texture"] = "AssetTextureBrush",
        ["Animation"] = "AssetAnimationBrush",
        ["Audio"] = "AssetAudioBrush",
        ["Mesh"] = "AssetMeshBrush",
        ["Material"] = "AssetMaterialBrush",
        ["Bone"] = "AssetBoneBrush",
        ["Container"] = "AssetContainerBrush",
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is string kind && s_keys.TryGetValue(kind, out string? found)
            ? found
            : "AssetUnknownBrush";

        return PaletteLookup.Brush(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
