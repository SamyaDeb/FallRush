using UnityEditor;
using UnityEngine;

/// <summary>
/// G3.2 import settings for our synthesised clips (Resources/FallRush/Audio): SFX / UI / chirps mono, decompressed
/// on load (instant, tiny); music stereo, compressed in memory. Unity's Web build re-encodes for the browser.
/// </summary>
public sealed class AudioImport : AssetPostprocessor
{
    private void OnPreprocessAudio()
    {
        if (!assetPath.Contains("/Resources/FallRush/Audio/"))
            return;
        var ai = (AudioImporter)assetImporter;
        var music = System.IO.Path.GetFileName(assetPath).StartsWith("music_");
        ai.forceToMono = !music;
        ai.loadInBackground = music;
        var s = ai.defaultSampleSettings;
        s.loadType = music ? AudioClipLoadType.CompressedInMemory : AudioClipLoadType.DecompressOnLoad;
        s.compressionFormat = AudioCompressionFormat.Vorbis;
        s.quality = music ? 0.55f : 0.5f;
        s.preloadAudioData = true;
        ai.defaultSampleSettings = s;
    }
}
