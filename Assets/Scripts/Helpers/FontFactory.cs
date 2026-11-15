using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

public static class FontFactory
{
    public static TMP_FontAsset CreateStaticTmpFont(
        string resourcesFontPath,
        string charactersToBake,
        int samplingPointSize = 90,
        int atlasPadding = 9,
        int atlasWidth = 1024,
        int atlasHeight = 1024)
    {
        Font sourceFont = Resources.Load<Font>(resourcesFontPath);
        if (!sourceFont)
        {
            Debug.LogError($"Font not found at Resources/{resourcesFontPath}");
            return null;
        }

        TMP_FontAsset fontAsset = TMP_FontAsset.CreateFontAsset(
            sourceFont,
            samplingPointSize,
            atlasPadding,
            GlyphRenderMode.SDFAA,
            atlasWidth,
            atlasHeight,
            AtlasPopulationMode.Dynamic
        );

        if (!string.IsNullOrEmpty(charactersToBake))
        {
            fontAsset.TryAddCharacters(charactersToBake, out string missingCharacters);
            if (!string.IsNullOrEmpty(missingCharacters))
            {
                Debug.LogWarning($"Missing glyphs in {sourceFont.name}: {missingCharacters}");
            }
        }

        fontAsset.atlasPopulationMode = AtlasPopulationMode.Static;

        return fontAsset;
    }
    
    // Fonts/PixelifySans-Regular.ttf. Editor loads it by path; a player build can only load Resources,
    // so a copy at Resources/Fonts/PixelifySans-Regular.ttf is the fallback.
    const string PixelifyPath = "Fonts/PixelifySans-Regular.ttf";
    const string PixelifyResourcesPath = "Fonts/PixelifySans-Regular";

    public static TMP_FontAsset GetPixelifyFont()
    {
        Font sourceFont = LoadPixelifyFont();
        if (!sourceFont)
        {
            Debug.LogError($"Font not found at Resources/{PixelifyResourcesPath}");
            return null;
        }
        return TMP_FontAsset.CreateFontAsset(sourceFont);
    }

    static Font LoadPixelifyFont() => Resources.Load<Font>(PixelifyResourcesPath);
}