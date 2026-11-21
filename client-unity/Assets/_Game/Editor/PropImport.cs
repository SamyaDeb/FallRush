using UnityEditor;

/// <summary>
/// G4.9: importer settings for the Blender prop kit (<c>Assets/_Game/Art/Props/*.fbx</c>, <c>tools/blender/props.py</c>):
/// readable meshes (MapArt merges them), no materials, animation, cameras, lights or tangents, file scale, axis
/// conversion baked. The colours are resolved later by <see cref="PropKit"/> (palette slots → both looks).
/// </summary>
public sealed class PropImport : AssetPostprocessor
{
    private void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(PropKit.Dir + "/"))
            return;
        var mi = (ModelImporter)assetImporter;
        mi.isReadable = true;
        mi.materialImportMode = ModelImporterMaterialImportMode.None;
        mi.importAnimation = false;
        mi.animationType = ModelImporterAnimationType.None;
        mi.importBlendShapes = false;
        mi.importCameras = false;
        mi.importLights = false;
        mi.importVisibility = false;
        mi.importTangents = ModelImporterTangents.None;
        mi.importNormals = ModelImporterNormals.Import;
        mi.generateSecondaryUV = false;
        mi.meshCompression = ModelImporterMeshCompression.Off;
        mi.bakeAxisConversion = true;
        mi.useFileScale = true;
        mi.globalScale = 1f;
    }
}
