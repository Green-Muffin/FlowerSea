using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FlowerSea.GlobalIllumination
{
    public class DDGIRendererFeature : ScriptableRendererFeature
    {
        public enum DebugMode
        {
            Off,
            IrradianceAtlas,
            DepthAtlas,
            SDFRaymarch
        }

        public DebugMode debugMode = DebugMode.Off;

        DDGIUpdatePass m_UpdatePass;
        DDGIDebugPass m_DebugPass;

        public override void Create()
        {
            m_UpdatePass = new DDGIUpdatePass();
            m_DebugPass = new DDGIDebugPass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            CameraType cameraType = renderingData.cameraData.cameraType;
            if (cameraType != CameraType.Game && cameraType != CameraType.SceneView)
                return;

            renderer.EnqueuePass(m_UpdatePass);
            if (debugMode != DebugMode.Off)
            {
                m_DebugPass.Mode = debugMode;
                renderer.EnqueuePass(m_DebugPass);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                m_DebugPass?.Dispose();
                DDGIManager.Instance.Release();
                DDGISdfManager.Shutdown();
            }
        }
    }

    class DDGIUpdatePass : ScriptableRenderPass
    {
        static readonly ProfilingSampler k_Profiler = new ProfilingSampler("DDGI.Update");

        public DDGIUpdatePass()
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, k_Profiler))
            {
                DDGIManager.Instance.Update(renderingData.cameraData.camera, cmd, renderingData.cameraData.renderer);
                DDGISdfManager.SetGlobals(cmd);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    class DDGIDebugPass : ScriptableRenderPass
    {
        static readonly ProfilingSampler k_Profiler = new ProfilingSampler("DDGI.Debug");

        public DDGIRendererFeature.DebugMode Mode { get; set; }

        Material m_Material;

        public DDGIDebugPass()
        {
            renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        }

        public void Dispose()
        {
            CoreUtils.Destroy(m_Material);
            m_Material = null;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, k_Profiler))
            {
                RTHandle target = renderingData.cameraData.renderer.cameraColorTargetHandle;
                if (Mode == DDGIRendererFeature.DebugMode.SDFRaymarch)
                {
                    RenderTexture sdf = DDGISdfManager.SdfTexture;
                    if (sdf != null && EnsureMaterial())
                    {
                        cmd.SetRenderTarget(target);
                        cmd.DrawProcedural(Matrix4x4.identity, m_Material, 0, MeshTopology.Triangles, 3, 1);
                    }
                }
                else
                {
                    RTHandle atlas = Mode == DDGIRendererFeature.DebugMode.IrradianceAtlas
                        ? DDGIManager.Instance.IrradianceAtlas
                        : DDGIManager.Instance.DepthAtlas;
                    if (atlas != null)
                        Blitter.BlitCameraTexture(cmd, atlas, target);
                }
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        bool EnsureMaterial()
        {
            if (m_Material != null)
                return true;
            Shader shader = Shader.Find("Hidden/DDGI/DebugSDF");
            if (shader == null)
                return false;
            m_Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }
    }
}
