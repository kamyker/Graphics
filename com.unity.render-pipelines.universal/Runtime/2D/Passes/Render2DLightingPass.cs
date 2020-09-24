using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine.Rendering;
using UnityEngine.Profiling;
using UnityEngine.Rendering.Universal;

namespace UnityEngine.Experimental.Rendering.Universal
{
    internal class Render2DLightingPass : ScriptableRenderPass, IRenderPass2D
    {
        private static readonly int k_HDREmulationScaleID = Shader.PropertyToID("_HDREmulationScale");
        private static readonly int k_InverseHDREmulationScaleID = Shader.PropertyToID("_InverseHDREmulationScale");
        private static readonly int k_UseSceneLightingID = Shader.PropertyToID("_UseSceneLighting");
        private static readonly int k_RendererColorID = Shader.PropertyToID("_RendererColor");

        private static readonly int[] k_ShapeLightTextureIDs =
        {
            Shader.PropertyToID("_ShapeLightTexture0"),
            Shader.PropertyToID("_ShapeLightTexture1"),
            Shader.PropertyToID("_ShapeLightTexture2"),
            Shader.PropertyToID("_ShapeLightTexture3")
        };

        private static readonly ShaderTagId k_CombinedRenderingPassNameOld = new ShaderTagId("Lightweight2D");
        private static readonly ShaderTagId k_CombinedRenderingPassName = new ShaderTagId("Universal2D");
        private static readonly ShaderTagId k_NormalsRenderingPassName = new ShaderTagId("NormalsRendering");
        private static readonly ShaderTagId k_LegacyPassName = new ShaderTagId("SRPDefaultUnlit");
        private static readonly List<ShaderTagId> k_ShaderTags = new List<ShaderTagId>() { k_LegacyPassName, k_CombinedRenderingPassName, k_CombinedRenderingPassNameOld, new ShaderTagId("UniversalForwardOnly") };

        private static readonly ProfilingSampler m_ProfilingDrawLights = new ProfilingSampler("Draw 2D Lights");
        private static readonly ProfilingSampler m_ProfilingDrawLightTextures = new ProfilingSampler("Draw 2D Lights Textures");
        private static readonly ProfilingSampler m_ProfilingDrawRenderers = new ProfilingSampler("Draw All Renderers");
        private static readonly ProfilingSampler m_ProfilingDrawLayerBatch = new ProfilingSampler("Draw Layer Batch");
        private static readonly ProfilingSampler m_ProfilingSamplerUnlit = new ProfilingSampler("Render Unlit");

        private readonly Renderer2DData m_Renderer2DData;
        private bool m_HasValidDepth;

        public Render2DLightingPass(Renderer2DData rendererData)
        {
            m_Renderer2DData = rendererData;
        }

        internal void Setup(bool hasValidDepth)
        {
            m_HasValidDepth = hasValidDepth;
        }

        private void GetTransparencySortingMode(Camera camera, ref SortingSettings sortingSettings)
        {
            var mode = camera.transparencySortMode;

            if (mode == TransparencySortMode.Default)
            {
                mode = m_Renderer2DData.transparencySortMode;
                if (mode == TransparencySortMode.Default)
                    mode = camera.orthographic ? TransparencySortMode.Orthographic : TransparencySortMode.Perspective;
            }

            if (mode == TransparencySortMode.Perspective)
            {
                sortingSettings.distanceMetric = DistanceMetric.Perspective;
            }
            else if (mode == TransparencySortMode.Orthographic)
            {
                sortingSettings.distanceMetric = DistanceMetric.Orthographic;
            }
            else
            {
                sortingSettings.distanceMetric = DistanceMetric.CustomAxis;
                sortingSettings.customAxis = m_Renderer2DData.transparencySortAxis;
            }
        }

        private void DrawRenderers(
            LayerBatch[] layerBatches,
            int startIndex,
            int batchSize,
            CommandBuffer cmd,
            ScriptableRenderContext context,
            ref RenderingData renderingData,
            FilteringSettings filterSettings,
            DrawingSettings drawSettings)
        {
            using(new ProfilingScope(cmd, m_ProfilingDrawRenderers))
            {
                // and the main render target
                CoreUtils.SetRenderTarget(cmd, colorAttachment, depthAttachment, ClearFlag.None, Color.white);

                for (var i = 0; i < batchSize; i++)
                {
                    ref var layerBatch = ref layerBatches[startIndex + i];

                    using(new ProfilingScope(cmd, m_ProfilingDrawLayerBatch))
                    {
                        if (layerBatch.lightStats.totalLights > 0)
                        {
                            unsafe
                            {
                                for (var blendStyleIndex = 0; blendStyleIndex < k_ShapeLightTextureIDs.Length; blendStyleIndex++)
                                {
                                    var used = layerBatch.renderTargetUsed[blendStyleIndex];

                                    if (used)
                                        cmd.SetGlobalTexture(k_ShapeLightTextureIDs[blendStyleIndex], new RenderTargetIdentifier(layerBatch.renderTargetIds[blendStyleIndex]));

                                    RendererLighting.EnableBlendStyle(cmd, blendStyleIndex, used);
                                }
                            }
                        }
                        else
                        {
                            for (var blendStyleIndex = 0; blendStyleIndex < k_ShapeLightTextureIDs.Length; blendStyleIndex++)
                            {
                                cmd.SetGlobalTexture(k_ShapeLightTextureIDs[blendStyleIndex], Texture2D.blackTexture);
                                RendererLighting.EnableBlendStyle(cmd, blendStyleIndex, blendStyleIndex == 0);
                            }
                        }

                        context.ExecuteCommandBuffer(cmd);
                        cmd.Clear();

                        filterSettings.sortingLayerRange = layerBatch.layerRange;
                        context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref filterSettings);

                        if (layerBatch.lightStats.totalVolumetricUsage > 0)
                        {
                            var sampleName = "Render 2D Light Volumes";
                            cmd.BeginSample(sampleName);
                            this.RenderLightVolumes(renderingData, cmd, layerBatch.firstLayerToRender, colorAttachment, depthAttachment, m_Renderer2DData.lightCullResult.visibleLights);
                            cmd.EndSample(sampleName);
                        }
                    }
                }
            }
        }

        private void DrawLightTextures(
            LayerBatch[] layerBatches,
            int startIndex,
            int batchSize,
            CommandBuffer cmd,
            ScriptableRenderContext context,
            ref RenderingData renderingData,
            FilteringSettings filterSettings,
            DrawingSettings normalsDrawSettings)
        {
            var blendStylesCount = m_Renderer2DData.lightBlendStyles.Length;

            using (new ProfilingScope(cmd, m_ProfilingDrawLights))
            {
                for (var i = 0; i < batchSize; i++)
                {
                    ref var layerBatch = ref layerBatches[startIndex + i];

                    if (layerBatch.lightStats.totalNormalMapUsage > 0)
                    {
                        filterSettings.sortingLayerRange = layerBatch.layerRange;
                        var depthTarget = m_HasValidDepth ? depthAttachment : BuiltinRenderTextureType.None;
                        this.RenderNormals(context, renderingData, normalsDrawSettings, filterSettings, depthTarget, cmd, layerBatch.lightStats);
                    }

                    using (new ProfilingScope(cmd, m_ProfilingDrawLightTextures))
                    {
                        // create the render texture ids
                        for (var blendStyleIndex = 0; blendStyleIndex < blendStylesCount; blendStyleIndex++)
                        {
                            unsafe
                            {
                                var blendStyleMask = (uint) (1 << blendStyleIndex);
                                var blendStyleUsed = (layerBatch.lightStats.blendStylesUsed & blendStyleMask) > 0;
                                layerBatch.renderTargetUsed[blendStyleIndex] = blendStyleUsed;

                                if (!blendStyleUsed)
                                    continue;

                                this.CreateBlendStyleRenderTexture(renderingData, cmd, blendStyleIndex, layerBatch.renderTargetIds[blendStyleIndex]);
                            }
                        }

                        this.RenderLights(renderingData, cmd, layerBatch.firstLayerToRender, layerBatch);
                    }
                }
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var isLitView = true;

#if UNITY_EDITOR
            if (renderingData.cameraData.isSceneViewCamera)
                isLitView = UnityEditor.SceneView.currentDrawingSceneView.sceneLighting;

            if (renderingData.cameraData.camera.cameraType == CameraType.Preview)
                isLitView = false;
#endif
            var camera = renderingData.cameraData.camera;
            var filterSettings = new FilteringSettings();
            filterSettings.renderQueueRange = RenderQueueRange.all;
            filterSettings.layerMask = -1;
            filterSettings.renderingLayerMask = 0xFFFFFFFF;
            filterSettings.sortingLayerRange = SortingLayerRange.all;

            var isSceneLit = m_Renderer2DData.lightCullResult.IsSceneLit();
            if (isSceneLit)
            {
                var cachedSortingLayers = Light2DManager.GetCachedSortingLayer();
                var layerBatches = LayerUtility.GetCachedLayerBatches();
                var combinedDrawSettings = CreateDrawingSettings(k_ShaderTags, ref renderingData, SortingCriteria.CommonTransparent);
                var normalsDrawSettings = CreateDrawingSettings(k_NormalsRenderingPassName, ref renderingData, SortingCriteria.CommonTransparent);

                var sortSettings = combinedDrawSettings.sortingSettings;
                GetTransparencySortingMode(camera, ref sortSettings);
                combinedDrawSettings.sortingSettings = sortSettings;
                normalsDrawSettings.sortingSettings = sortSettings;

                var cmd = CommandBufferPool.Get();
                cmd.SetGlobalFloat(k_HDREmulationScaleID, m_Renderer2DData.hdrEmulationScale);
                cmd.SetGlobalFloat(k_InverseHDREmulationScaleID, 1.0f / m_Renderer2DData.hdrEmulationScale);
                cmd.SetGlobalFloat(k_UseSceneLightingID, isLitView ? 1.0f : 0.0f);
                cmd.SetGlobalColor(k_RendererColorID, Color.white);
                this.SetShapeLightShaderGlobals(cmd);

                var batchCount = LayerUtility.CalculateBatches(cachedSortingLayers, layerBatches, m_Renderer2DData.lightCullResult);
                var batchSize = m_Renderer2DData.batchSize;
                for (var i = 0; i < batchCount; i += batchSize)
                {
                    var effectiveBatchSize = math.min(batchSize, batchCount - i);
                    DrawLightTextures(layerBatches, i, effectiveBatchSize, cmd, context, ref renderingData, filterSettings, normalsDrawSettings);
                    DrawRenderers(layerBatches, i, effectiveBatchSize, cmd, context, ref renderingData, filterSettings, combinedDrawSettings);
                }

                this.ReleaseRenderTextures(cmd, layerBatches);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
            else
            {
                var unlitDrawSettings = CreateDrawingSettings(k_ShaderTags, ref renderingData, SortingCriteria.CommonTransparent);

                var cmd = CommandBufferPool.Get();
                using (new ProfilingScope(cmd, m_ProfilingSamplerUnlit))
                {
                    CoreUtils.SetRenderTarget(cmd, colorAttachment, depthAttachment, ClearFlag.None, Color.white);
                    for (var i = 0; i < k_ShapeLightTextureIDs.Length; i++)
                    {
                        cmd.SetGlobalTexture(k_ShapeLightTextureIDs[i], Texture2D.blackTexture);
                    }
                    cmd.SetGlobalFloat(k_UseSceneLightingID, isLitView ? 1.0f : 0.0f);
                    cmd.SetGlobalColor(k_RendererColorID, Color.white);
                    cmd.EnableShaderKeyword("USE_SHAPE_LIGHT_TYPE_0");
                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);

                Profiler.BeginSample("Render Sprites Unlit");
                    context.DrawRenderers(renderingData.cullResults, ref unlitDrawSettings, ref filterSettings);
                Profiler.EndSample();
            }

            filterSettings.sortingLayerRange = SortingLayerRange.all;
            RenderingUtils.RenderObjectsWithError(context, ref renderingData.cullResults, camera, filterSettings, SortingCriteria.None);
        }

        Renderer2DData IRenderPass2D.rendererData
        {
            get { return m_Renderer2DData; }
        }
    }
}
