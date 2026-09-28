using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace FlowerSea.GlobalIllumination
{
    public static class DDGISdfManager
    {
        static RenderTexture s_SdfTexture;
        static Vector3 s_Origin;
        static float s_VoxelSize = 0.5f;
        static Vector3Int s_TexSize;

        public static RenderTexture SdfTexture => s_SdfTexture;
        public static Vector3 Origin => s_Origin;
        public static float VoxelSize => s_VoxelSize;
        public static Vector3Int TexSize => s_TexSize;

        public static bool Bake(DDGIVolume volume, ComputeShader shader)
        {
            if (volume == null || shader == null)
                return false;

            List<float> triangles = CollectTriangles(volume);
            if (triangles.Count < 9)
            {
                Debug.LogWarning("[DDGI] SDF bake: no triangles collected, check sdfGeometryRoot");
                return false;
            }

            Vector3 size = volume.Size;
            Vector3Int counts = new Vector3Int(
                Mathf.Clamp(Mathf.CeilToInt(size.x / volume.sdfVoxelSize), 1, 256),
                Mathf.Clamp(Mathf.CeilToInt(size.y / volume.sdfVoxelSize), 1, 256),
                Mathf.Clamp(Mathf.CeilToInt(size.z / volume.sdfVoxelSize), 1, 256));
            Vector3 origin = volume.transform.position - size * 0.5f;

            ReleaseTexture();
            s_SdfTexture = CreateTexture(counts);
            s_Origin = origin;
            s_VoxelSize = volume.sdfVoxelSize;
            s_TexSize = counts;

            int triangleCount = triangles.Count / 9;
            int voxelTotal = counts.x * counts.y * counts.z;

            ComputeBuffer triangleBuffer = new ComputeBuffer(triangles.Count, 4);
            ComputeBuffer countBuffer = new ComputeBuffer(voxelTotal, 4);
            ComputeBuffer slotBuffer = new ComputeBuffer(voxelTotal * 12, 4);

            try
            {
                triangleBuffer.SetData(triangles);

                int clearKernel = shader.FindKernel("ClearCounts");
                int rasterKernel = shader.FindKernel("RasterizeTriangles");
                int buildKernel = shader.FindKernel("BuildSDF");

                shader.SetBuffer(clearKernel, "_Counts", countBuffer);
                shader.SetBuffer(rasterKernel, "_Counts", countBuffer);
                shader.SetBuffer(rasterKernel, "_Slots", slotBuffer);
                shader.SetBuffer(rasterKernel, "_Triangles", triangleBuffer);
                shader.SetBuffer(buildKernel, "_Counts", countBuffer);
                shader.SetBuffer(buildKernel, "_Slots", slotBuffer);
                shader.SetBuffer(buildKernel, "_Triangles", triangleBuffer);
                shader.SetTexture(buildKernel, "_Out", s_SdfTexture);

                shader.SetInteger("_TriangleCount", triangleCount);
                shader.SetVector("_VoxelCounts", new Vector4(counts.x, counts.y, counts.z, 0));
                shader.SetVector("_Origin", origin);
                shader.SetFloat("_VoxelSize", volume.sdfVoxelSize);

                shader.Dispatch(clearKernel, Mathf.CeilToInt(voxelTotal / 64f), 1, 1);
                shader.Dispatch(rasterKernel, Mathf.CeilToInt(triangleCount / 64f), 1, 1);
                shader.Dispatch(buildKernel,
                    Mathf.CeilToInt(counts.x / 4f),
                    Mathf.CeilToInt(counts.y / 4f),
                    Mathf.CeilToInt(counts.z / 4f));
            }
            finally
            {
                triangleBuffer.Release();
                countBuffer.Release();
                slotBuffer.Release();
            }

            Debug.Log($"[DDGI] SDF baked: {triangleCount} triangles, grid {counts.x}x{counts.y}x{counts.z} @ {volume.sdfVoxelSize:0.00}m");
            return true;
        }

        public static void SetGlobals(CommandBuffer cmd)
        {
            if (s_SdfTexture == null)
                return;
            cmd.SetGlobalTexture(DDGIShaderIDs.Sdf, s_SdfTexture);
            cmd.SetGlobalVector(DDGIShaderIDs.SdfOriginSize, new Vector4(s_Origin.x, s_Origin.y, s_Origin.z, s_VoxelSize));
            cmd.SetGlobalVector(DDGIShaderIDs.SdfTexSize, new Vector4(s_TexSize.x, s_TexSize.y, s_TexSize.z, 0));
        }

        public static void Shutdown()
        {
            ReleaseTexture();
        }

        static void ReleaseTexture()
        {
            if (s_SdfTexture != null)
            {
                s_SdfTexture.Release();
                CoreUtils.Destroy(s_SdfTexture);
                s_SdfTexture = null;
            }
        }

        static RenderTexture CreateTexture(Vector3Int counts)
        {
            var descriptor = new RenderTextureDescriptor(counts.x, counts.y, GraphicsFormat.R32_SFloat, 0)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = counts.z,
                enableRandomWrite = true,
                msaaSamples = 1,
                useMipMap = false
            };
            var rt = new RenderTexture(descriptor)
            {
                name = "DDGI.SDF",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            rt.Create();
            return rt;
        }

        static List<float> CollectTriangles(DDGIVolume volume)
        {
            var triangles = new List<float>(1 << 16);
            if (volume.sdfGeometryRoot == null)
            {
                Debug.LogWarning("[DDGI] SDF bake: sdfGeometryRoot is not assigned on DDGIVolume");
                return triangles;
            }

            MeshRenderer[] renderers = volume.sdfGeometryRoot.GetComponentsInChildren<MeshRenderer>(false);
            foreach (MeshRenderer renderer in renderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;

                bool transparent = false;
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material != null && material.renderQueue >= 3000)
                    {
                        transparent = true;
                        break;
                    }
                }
                if (transparent)
                    continue;

                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null)
                    continue;

                Vector3[] vertices = mesh.vertices;
                int[] indices = mesh.triangles;
                Matrix4x4 matrix = renderer.localToWorldMatrix;

                for (int i = 0; i < indices.Length; i++)
                {
                    Vector3 v = matrix.MultiplyPoint3x4(vertices[indices[i]]);
                    triangles.Add(v.x);
                    triangles.Add(v.y);
                    triangles.Add(v.z);
                }
            }
            return triangles;
        }
    }
}
