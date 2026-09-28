using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FlowerSea.GlobalIllumination
{
    internal static class DDGIShaderIDs
    {
        public static readonly int Irradiance = Shader.PropertyToID("_DDGIIrradiance");
        public static readonly int Depth = Shader.PropertyToID("_DDGIDepth");
        public static readonly int Origin = Shader.PropertyToID("_DDGIOrigin");
        public static readonly int Spacing = Shader.PropertyToID("_DDGISpacing");
        public static readonly int ProbeCounts = Shader.PropertyToID("_DDGIProbeCounts");
        public static readonly int IrradianceTexSize = Shader.PropertyToID("_DDGIIrradianceTexSize");
        public static readonly int DepthTexSize = Shader.PropertyToID("_DDGIDepthTexSize");
        public static readonly int Sdf = Shader.PropertyToID("_DDGISDF");
        public static readonly int SdfOriginSize = Shader.PropertyToID("_DDGISDFOriginSize");
        public static readonly int SdfTexSize = Shader.PropertyToID("_DDGISDFTexSize");
    }

    public class DDGIManager
    {
        static DDGIManager s_Instance;
        public static DDGIManager Instance => s_Instance ?? (s_Instance = new DDGIManager());

        DDGIVolume m_Volume;
        int m_SettingsHash;
        Vector3 m_Origin;
        RenderTexture m_Irradiance;
        RenderTexture m_Depth;
        RTHandle m_IrradianceHandle;
        RTHandle m_DepthHandle;

        public RTHandle IrradianceAtlas => m_IrradianceHandle;
        public RTHandle DepthAtlas => m_DepthHandle;
        public Vector3 Origin => m_Origin;
        public int TotalProbes => m_Volume != null ? m_Volume.probeCounts.x * m_Volume.probeCounts.y * m_Volume.probeCounts.z : 0;

        public DDGIVolume ResolveVolume()
        {
            if (m_Volume != null && m_Volume.enabled && m_Volume.gameObject.activeInHierarchy)
                return m_Volume;
            m_Volume = Object.FindObjectOfType<DDGIVolume>();
            m_SettingsHash = 0;
            return m_Volume;
        }

        public void Update(Camera camera, CommandBuffer cmd, ScriptableRenderer renderer)
        {
            DDGIVolume volume = ResolveVolume();
            if (volume == null || camera == null)
                return;

            if (m_Irradiance == null || m_Depth == null || m_SettingsHash != ComputeSettingsHash(volume))
                Rebuild(volume);

            Vector3 target = ComputeOrigin(volume, camera);
            if (target != m_Origin)
            {
                m_Origin = target;
                ClearAtlases(cmd, renderer);
            }

            SetGlobals(cmd);
        }

        public void Release()
        {
            if (m_IrradianceHandle != null)
                RTHandles.Release(m_IrradianceHandle);
            if (m_DepthHandle != null)
                RTHandles.Release(m_DepthHandle);
            CoreUtils.Destroy(m_Irradiance);
            CoreUtils.Destroy(m_Depth);
            m_Irradiance = null;
            m_Depth = null;
            m_IrradianceHandle = null;
            m_DepthHandle = null;
            m_SettingsHash = 0;
        }

        Vector3 ComputeOrigin(DDGIVolume volume, Camera camera)
        {
            Vector3 size = volume.Size;
            Vector3 half = size * 0.5f;
            Vector3 anchor = volume.transform.position - half;
            Vector3 focus = camera.transform.position;
            if (volume.lockVertical)
                focus.y = volume.transform.position.y;

            Vector3 origin = focus - half;
            origin.x = anchor.x + Mathf.Round((origin.x - anchor.x) / volume.spacing) * volume.spacing;
            origin.y = anchor.y + Mathf.Round((origin.y - anchor.y) / volume.spacing) * volume.spacing;
            origin.z = anchor.z + Mathf.Round((origin.z - anchor.z) / volume.spacing) * volume.spacing;
            return origin;
        }

        void Rebuild(DDGIVolume volume)
        {
            Release();
            int irradiancePitch = volume.irradianceTexels + volume.irradianceBorder * 2;
            int depthPitch = volume.depthTexels + volume.depthBorder * 2;
            m_Irradiance = CreateAtlas(volume, irradiancePitch, GraphicsFormat.R16G16B16A16_SFloat, "DDGI.IrradianceAtlas");
            m_Depth = CreateAtlas(volume, depthPitch, GraphicsFormat.R16G16_SFloat, "DDGI.DepthAtlas");
            m_IrradianceHandle = RTHandles.Alloc(m_Irradiance);
            m_DepthHandle = RTHandles.Alloc(m_Depth);
            m_SettingsHash = ComputeSettingsHash(volume);
            m_Origin = Vector3.positiveInfinity;
        }

        RenderTexture CreateAtlas(DDGIVolume volume, int pitch, GraphicsFormat format, string name)
        {
            var descriptor = new RenderTextureDescriptor(
                volume.probeCounts.x * pitch,
                volume.probeCounts.y * volume.probeCounts.z * pitch)
            {
                graphicsFormat = format,
                depthBufferBits = 0,
                msaaSamples = 1,
                useMipMap = false,
                enableRandomWrite = true
            };
            var rt = new RenderTexture(descriptor)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            rt.Create();
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = previous;
            return rt;
        }

        void ClearAtlases(CommandBuffer cmd, ScriptableRenderer renderer)
        {
            cmd.SetRenderTarget(m_IrradianceHandle);
            cmd.ClearRenderTarget(false, true, Color.clear);
            cmd.SetRenderTarget(m_DepthHandle);
            cmd.ClearRenderTarget(false, true, Color.clear);
            if (renderer != null)
                cmd.SetRenderTarget(renderer.cameraColorTargetHandle);
        }

        void SetGlobals(CommandBuffer cmd)
        {
            DDGIVolume volume = m_Volume;
            cmd.SetGlobalTexture(DDGIShaderIDs.Irradiance, m_IrradianceHandle);
            cmd.SetGlobalTexture(DDGIShaderIDs.Depth, m_DepthHandle);
            cmd.SetGlobalVector(DDGIShaderIDs.Origin, m_Origin);
            cmd.SetGlobalFloat(DDGIShaderIDs.Spacing, volume.spacing);
            cmd.SetGlobalVector(DDGIShaderIDs.ProbeCounts, new Vector4(
                volume.probeCounts.x, volume.probeCounts.y, volume.probeCounts.z, TotalProbes));
            cmd.SetGlobalVector(DDGIShaderIDs.IrradianceTexSize, new Vector4(
                m_Irradiance.width, m_Irradiance.height, volume.irradianceTexels, volume.irradianceBorder));
            cmd.SetGlobalVector(DDGIShaderIDs.DepthTexSize, new Vector4(
                m_Depth.width, m_Depth.height, volume.depthTexels, volume.depthBorder));
        }

        int ComputeSettingsHash(DDGIVolume volume)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + volume.probeCounts.x;
                hash = hash * 31 + volume.probeCounts.y;
                hash = hash * 31 + volume.probeCounts.z;
                hash = hash * 31 + volume.irradianceTexels;
                hash = hash * 31 + volume.depthTexels;
                hash = hash * 31 + volume.irradianceBorder;
                hash = hash * 31 + volume.depthBorder;
                return hash;
            }
        }
    }
}
