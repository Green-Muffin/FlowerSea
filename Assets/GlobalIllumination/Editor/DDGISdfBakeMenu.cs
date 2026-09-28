using UnityEditor;
using UnityEngine;
using FlowerSea.GlobalIllumination;

static class DDGISdfBakeMenu
{
    const string k_ComputeShaderPath = "Assets/GlobalIllumination/Shaders/DDGISdfBuild.compute";

    [MenuItem("Tools/DDGI/Bake SDF")]
    static void Bake()
    {
        DDGIVolume volume = Object.FindObjectOfType<DDGIVolume>();
        if (volume == null)
        {
            Debug.LogWarning("[DDGI] No DDGIVolume found in open scene");
            return;
        }

        ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(k_ComputeShaderPath);
        if (shader == null)
        {
            Debug.LogWarning($"[DDGI] Compute shader not found at {k_ComputeShaderPath}");
            return;
        }

        if (DDGISdfManager.Bake(volume, shader))
            SceneView.RepaintAll();
    }
}
