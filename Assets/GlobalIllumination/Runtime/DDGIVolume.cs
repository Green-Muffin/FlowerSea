using UnityEngine;

namespace FlowerSea.GlobalIllumination
{
    [DisallowMultipleComponent]
    [AddComponentMenu("DDGI/DDGI Volume")]
    public class DDGIVolume : MonoBehaviour
    {
        public Vector3Int probeCounts = new Vector3Int(32, 16, 32);
        public float spacing = 1.0f;
        public bool lockVertical = true;

        [Header("Update")]
        [Range(0.0f, 0.35f)] public float irradianceBlend = 0.08f;
        [Range(0.0f, 0.35f)] public float depthBlend = 0.04f;
        public int raysPerProbe = 256;
        [Range(1, 8)] public int probeUpdateDivisor = 4;

        [Header("Octahedral Maps")]
        [Range(4, 16)] public int irradianceTexels = 8;
        [Range(8, 32)] public int depthTexels = 16;
        public int irradianceBorder = 1;
        public int depthBorder = 3;

        [Header("Sampling")]
        [Range(0.0f, 1.0f)] public float normalBias = 0.3f;
        [Range(0.0f, 1.0f)] public float viewBias = 0.4f;
        [Min(0.0f)] public float energyScale = 1.0f;

        [Header("SDF")]
        public Transform sdfGeometryRoot;
        [Min(0.05f)] public float sdfVoxelSize = 0.5f;

        public Vector3 Size => Vector3.Scale(new Vector3(probeCounts.x, probeCounts.y, probeCounts.z), Vector3.one * spacing);

        public Vector3 ProbeLocalPosition(Vector3Int index)
        {
            Vector3 half = Size * 0.5f;
            return new Vector3(
                (index.x + 0.5f) * spacing - half.x,
                (index.y + 0.5f) * spacing - half.y,
                (index.z + 0.5f) * spacing - half.z);
        }

        void OnDrawGizmosSelected()
        {
            Vector3 size = Size;
            Matrix4x4 oldMatrix = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.25f, 0.9f, 0.55f, 1.0f);
            Gizmos.DrawWireCube(Vector3.zero, size);

            Vector3Int stride = GetGizmoStride();
            Gizmos.color = new Color(0.95f, 0.75f, 0.2f, 1.0f);
            Vector3 dotSize = Vector3.one * spacing * 0.08f;
            for (int z = 0; z < probeCounts.z; z += stride.z)
            {
                for (int y = 0; y < probeCounts.y; y += stride.y)
                {
                    for (int x = 0; x < probeCounts.x; x += stride.x)
                    {
                        Gizmos.DrawCube(ProbeLocalPosition(new Vector3Int(x, y, z)), dotSize);
                    }
                }
            }
            Gizmos.matrix = oldMatrix;
        }

        Vector3Int GetGizmoStride()
        {
            const int MaxDotsPerAxis = 10;
            return new Vector3Int(
                Mathf.Max(1, Mathf.CeilToInt(probeCounts.x / (float)MaxDotsPerAxis)),
                Mathf.Max(1, Mathf.CeilToInt(probeCounts.y / (float)MaxDotsPerAxis)),
                Mathf.Max(1, Mathf.CeilToInt(probeCounts.z / (float)MaxDotsPerAxis)));
        }
    }
}
