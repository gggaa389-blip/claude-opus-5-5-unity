using UnityEditor;
using UnityEngine;

/// The recorded sound effects (Assets/Resources/Sfx): short, played many times a second in a fight, so
/// mono, uncompressed and decompressed on load (no decoding when a shot fires, no hitch on the first one).
public class IslandAudioImport : AssetPostprocessor
{
    void OnPreprocessAudio()
    {
        if (!assetPath.StartsWith("Assets/Resources/Sfx/")) return;
        var ai = (AudioImporter)assetImporter;
        ai.forceToMono = true;
        ai.loadInBackground = false;
        var s = ai.defaultSampleSettings;
        s.loadType = AudioClipLoadType.DecompressOnLoad;
        s.compressionFormat = AudioCompressionFormat.PCM;
        s.preloadAudioData = true;
        ai.defaultSampleSettings = s;
    }
}
