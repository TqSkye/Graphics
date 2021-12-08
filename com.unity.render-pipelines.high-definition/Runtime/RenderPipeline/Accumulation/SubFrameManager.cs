using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

#if UNITY_EDITOR
using UnityEditor;
#endif // UNITY_EDITOR

namespace UnityEngine.Rendering.HighDefinition
{
    // Struct storing per-camera data, to handle accumulation and dirtiness
    internal struct CameraData
    {
        public void ResetIteration()
        {
            accumulatedWeight = 0.0f;
            currentIteration = 0;
#if ENABLE_UNITY_DENOISERS
            if (denoiser != null)
                denoiser.Reset();
            else
                denoiser = new Denoiser();
#endif
        }

        public uint width;
        public uint height;
        public bool skyEnabled;
        public bool fogEnabled;
        public ulong accelSize;

        public float accumulatedWeight;
        public uint currentIteration;
#if ENABLE_UNITY_DENOISERS
        public Denoiser denoiser;
#endif
    }

    // Helper class to manage time-scale in Unity when recording multi-frame sequences where one final frame is an accumulation of multiple sub-frames
    internal class SubFrameManager
    {
        // Shutter settings
        float m_ShutterInterval = 0.0f;
        float m_ShutterFullyOpen = 0.0f;
        float m_ShutterBeginsClosing = 1.0f;

        AnimationCurve m_ShutterCurve;

        // Internal state
        float m_OriginalCaptureDeltaTime = 0;
        float m_OriginalFixedDeltaTime = 0;

        // Per-camera data cache
        Dictionary<int, CameraData> m_CameraCache = new Dictionary<int, CameraData>();

        internal CameraData GetCameraData(int camID)
        {
            CameraData camData;
            if (!m_CameraCache.TryGetValue(camID, out camData))
            {
                camData.ResetIteration();
                m_CameraCache.Add(camID, camData);
            }
            return camData;
        }

        internal void SetCameraData(int camID, CameraData camData)
        {
            m_CameraCache[camID] = camData;
        }

        // The number of sub-frames that will be used to reconstruct a converged frame
        public uint subFrameCount
        {
            get { return m_AccumulationSamples; }
            set { m_AccumulationSamples = value; }
        }
        uint m_AccumulationSamples = 0;

        // True when a recording session is in progress
        public bool isRecording
        {
            get { return m_IsRecording; }
        }
        bool m_IsRecording = false;

        // Resets the sub-frame sequence
        internal void Reset(int camID)
        {
            CameraData camData = GetCameraData(camID);
            camData.ResetIteration();
            SetCameraData(camID, camData);
        }

        internal void Reset()
        {
            foreach (int camID in m_CameraCache.Keys.ToList())
                Reset(camID);
        }

        internal void Clear()
        {
            m_CameraCache.Clear();
        }

        internal void SelectiveReset(uint maxSamples)
        {
            foreach (int camID in m_CameraCache.Keys.ToList())
            {
                CameraData camData = GetCameraData(camID);
                if (camData.currentIteration >= maxSamples)
                {
                    camData.ResetIteration();
                    SetCameraData(camID, camData);
                }
            }
        }

        void Init(int samples, float shutterInterval)
        {
            m_AccumulationSamples = (uint)samples;
            m_ShutterInterval = samples > 1 ? shutterInterval : 0;
            m_IsRecording = true;

            Clear();

            m_OriginalCaptureDeltaTime = Time.captureDeltaTime;
            Time.captureDeltaTime = m_OriginalCaptureDeltaTime / m_AccumulationSamples;

            // This is required for physics simulations
            m_OriginalFixedDeltaTime = Time.fixedDeltaTime;
            Time.fixedDeltaTime = m_OriginalFixedDeltaTime / m_AccumulationSamples;
        }

        internal void BeginRecording(int samples, float shutterInterval, float shutterFullyOpen = 0.0f, float shutterBeginsClosing = 1.0f)
        {
            Init(samples, shutterInterval);

            m_ShutterFullyOpen = shutterFullyOpen;
            m_ShutterBeginsClosing = shutterBeginsClosing;
        }

        internal void BeginRecording(int samples, float shutterInterval, AnimationCurve shutterProfile)
        {
            Init(samples, shutterInterval);

            m_ShutterCurve = shutterProfile;
        }

        internal void EndRecording()
        {
            m_IsRecording = false;
            Time.captureDeltaTime = m_OriginalCaptureDeltaTime;
            Time.fixedDeltaTime = m_OriginalFixedDeltaTime;
            m_ShutterCurve = null;
        }

        // Should be called before rendering a new frame in a sequence (when accumulation is desired)
        internal void PrepareNewSubFrame()
        {
            uint maxIteration = 0;
            foreach (int camID in m_CameraCache.Keys.ToList())
                maxIteration = Math.Max(maxIteration, GetCameraData(camID).currentIteration);

            if (maxIteration >= m_AccumulationSamples)
            {
                Reset();
            }
        }

        // Helper function to compute the weight of a frame for a specific point in time
        float ShutterProfile(float time)
        {
            if (time > m_ShutterInterval)
            {
                return 0;
            }

            // Scale the subframe time so the m_ShutterInterval spans between 0 and 1
            time = time / m_ShutterInterval;

            // In case we have a curve profile, use this and return
            if (m_ShutterCurve != null)
            {
                return m_ShutterCurve.Evaluate(time);
            }

            // Otherwise use linear open and closing times
            if (time < m_ShutterFullyOpen)
            {
                float openingSlope = 1.0f / m_ShutterFullyOpen;
                return openingSlope * time;
            }
            else if (time > m_ShutterBeginsClosing)
            {
                float closingSlope = 1.0f / (1.0f - m_ShutterBeginsClosing);
                return 1.0f - closingSlope * (time - m_ShutterBeginsClosing);
            }
            else
            {
                return 1.0f;
            }
        }

        // returns the accumulation weights for the current sub-frame
        // x: weight for the current frame
        // y: sum of weights until now, without the current frame
        // z: one over the sum of weights until now, including the current frame
        // w: unused
        internal Vector4 ComputeFrameWeights(int camID)
        {
            CameraData camData = GetCameraData(camID);

            float totalWeight = camData.accumulatedWeight;
            float time = m_AccumulationSamples > 0 ? (float)camData.currentIteration / m_AccumulationSamples : 0.0f;

            float weight = isRecording ? ShutterProfile(time) : 1.0f;

            if (camData.currentIteration < m_AccumulationSamples)
                camData.accumulatedWeight += weight;

            SetCameraData(camID, camData);

            return (camData.accumulatedWeight > 0) ?
                new Vector4(weight, totalWeight, 1.0f / camData.accumulatedWeight, 0.0f) :
                new Vector4(weight, totalWeight, 0.0f, 0.0f);
        }
    }


    public partial class HDRenderPipeline
    {
        SubFrameManager m_SubFrameManager = new SubFrameManager();

        // Public API for multi-frame recording

        /// <summary>
        /// Should be called to start a multi-frame recording session. Each final frame will be an accumulation of multiple sub-frames.
        /// </summary>
        /// <param name="samples">The number of subframes. Each recorded frame will be an accumulation of this number of framesIn case path tracing is enabled, this value will override the settign in the volume.</param>
        /// <param name="shutterInterval">The duration the shutter of the virtual camera is open (for motion blur). Between 0 and 1.</param>
        /// <param name="shutterFullyOpen">The time it takes for the shutter to fully open. Between 0 and 1.</param>
        /// <param name="shutterBeginsClosing">The time when the shutter starts closing. Between 0 and 1.</param>
        public void BeginRecording(int samples, float shutterInterval, float shutterFullyOpen = 0.0f, float shutterBeginsClosing = 1.0f)
        {
            m_SubFrameManager.BeginRecording(samples, shutterInterval, shutterFullyOpen, shutterBeginsClosing);
        }

        /// <summary>
        /// Should be called to start a multi-frame recording session. Each final frame will be an accumulation of multiple sub-frames.
        /// </summary>
        /// <param name="samples">The number of subframes. Each recorded frame will be an accumulation of this number of frames. In case path tracing is enabled, this value will override the settign in the volume.</param>
        /// <param name="shutterInterval">The duration the shutter of the virtual camera is open (for motion blur). Between 0 and 1.</param>
        /// <param name="shutterProfile">An animation curve (between 0 and 1) denoting the motion of the camera shutter.</param>
        public void BeginRecording(int samples, float shutterInterval, AnimationCurve shutterProfile)
        {
            m_SubFrameManager.BeginRecording(samples, shutterInterval, shutterProfile);
        }

        /// <summary>
        /// Should be called to finish a multi-frame recording session
        /// </summary>
        public void EndRecording()
        {
            m_SubFrameManager.EndRecording();
        }

        /// <summary>
        /// Should be called during a recording session when preparing to render a new sub-frame of a multi-frame sequence where each final frame is an accumulation of multiple sub-frames.
        /// </summary>
        public void PrepareNewSubFrame()
        {
            m_SubFrameManager.PrepareNewSubFrame();
        }

        /// <summary>
        /// Checks if the multi-frame accumulation is completed for a given camera.
        /// </summary>
        /// <param name="hdCamera">Camera for which the accumulation status is checked.</param>
        /// <returns><c>true</c> if the accumulation is completed, <c>false</c> otherwise.</returns>
        public bool IsFrameCompleted(HDCamera hdCamera)
        {
            int camID = hdCamera.camera.GetInstanceID();
            CameraData camData = m_SubFrameManager.GetCameraData(camID);
            return camData.currentIteration >= m_SubFrameManager.subFrameCount;
        }

        class RenderAccumulationPassData
        {
            public ComputeShader accumulationCS;
            public int accumulationKernel;
            public SubFrameManager subFrameManager;
            public bool needExposure;
            public HDCamera hdCamera;

            public TextureHandle input;
            public TextureHandle output;
            public TextureHandle history;

            public bool useInputTexture;
            public bool useOutputTexture;

            public LocalKeyword inputKeyword;
            public LocalKeyword outputKeyword;
        }

        void RenderAccumulation(RenderGraph renderGraph, HDCamera hdCamera, TextureHandle inputTexture, TextureHandle outputTexture, HDCameraFrameHistoryType historyType, bool needExposure)
        {
            using (var builder = renderGraph.AddRenderPass<RenderAccumulationPassData>("Render Accumulation", out var passData))
            {
                bool useInputTexture = !inputTexture.Equals(outputTexture);
                passData.accumulationCS = m_Asset.renderPipelineResources.shaders.accumulationCS;
                passData.accumulationKernel = passData.accumulationCS.FindKernel("KMain");
                passData.subFrameManager = m_SubFrameManager;
                passData.needExposure = needExposure;
                passData.hdCamera = hdCamera;
                passData.inputKeyword = new LocalKeyword(passData.accumulationCS, "INPUT_FROM_FRAME_TEXTURE");
                passData.outputKeyword = new LocalKeyword(passData.accumulationCS, "WRITE_TO_OUTPUT_TEXTURE");

                TextureHandle history = renderGraph.ImportTexture(hdCamera.GetCurrentFrameRT((int)historyType)
                    ?? hdCamera.AllocHistoryFrameRT((int)historyType, PathTracingHistoryBufferAllocatorFunction, 1));

                passData.input = builder.ReadTexture(inputTexture);
                passData.history = builder.ReadWriteTexture(history);
                passData.useOutputTexture = outputTexture.IsValid();
                passData.useInputTexture = useInputTexture;

                if (outputTexture.IsValid())
                    passData.output = builder.ReadWriteTexture(outputTexture);

                builder.SetRenderFunc(
                    (RenderAccumulationPassData data, RenderGraphContext ctx) =>
                    {
                        ComputeShader accumulationShader = data.accumulationCS;

                        // Check the validity of the state before moving on with the computation
                        if (!accumulationShader)
                            return;

                        accumulationShader.shaderKeywords = null;
                        if (data.useInputTexture)
                            ctx.cmd.EnableKeyword(accumulationShader, passData.inputKeyword);
                        else
                            ctx.cmd.DisableKeyword(accumulationShader, passData.inputKeyword);

                        if (data.useOutputTexture)
                            ctx.cmd.EnableKeyword(accumulationShader, passData.outputKeyword);
                        else
                            ctx.cmd.DisableKeyword(accumulationShader, passData.outputKeyword);

                        // Get the per-camera data
                        int camID = data.hdCamera.camera.GetInstanceID();
                        Vector4 frameWeights = data.subFrameManager.ComputeFrameWeights(camID);
                        CameraData camData = data.subFrameManager.GetCameraData(camID);

                        // Accumulate the path tracing results
                        ctx.cmd.SetComputeIntParam(accumulationShader, HDShaderIDs._AccumulationFrameIndex, (int)camData.currentIteration);
                        ctx.cmd.SetComputeIntParam(accumulationShader, HDShaderIDs._AccumulationNumSamples, (int)data.subFrameManager.subFrameCount);
                        ctx.cmd.SetComputeTextureParam(accumulationShader, data.accumulationKernel, HDShaderIDs._AccumulatedFrameTexture, data.history);

                        if (data.useOutputTexture)
                            ctx.cmd.SetComputeTextureParam(accumulationShader, data.accumulationKernel, HDShaderIDs._CameraColorTextureRW, data.output);

                        if (data.useInputTexture)
                            ctx.cmd.SetComputeTextureParam(accumulationShader, data.accumulationKernel, HDShaderIDs._FrameTexture, data.input);

                        ctx.cmd.SetComputeVectorParam(accumulationShader, HDShaderIDs._AccumulationWeights, frameWeights);
                        ctx.cmd.SetComputeIntParam(accumulationShader, HDShaderIDs._AccumulationNeedsExposure, data.needExposure ? 1 : 0);
                        ctx.cmd.DispatchCompute(accumulationShader, data.accumulationKernel, (data.hdCamera.actualWidth + 7) / 8, (data.hdCamera.actualHeight + 7) / 8, data.hdCamera.viewCount);

                        // Increment the iteration counter, if we haven't converged yet
                        if (data.useOutputTexture && camData.currentIteration < data.subFrameManager.subFrameCount)
                        {
                            camData.currentIteration++;
                            data.subFrameManager.SetCameraData(camID, camData);
                        }
                    });
            }
        }

        class RenderDenoisePassData
        {
            public ComputeShader blitAndExposeCS;
            public int blitAndExposeKernel;
            public TextureHandle color;
            public TextureHandle normalAOV;
            public TextureHandle albedoAOV;
            public TextureHandle motionVectorAOV;
            public TextureHandle outputTexture;
            public SubFrameManager subFrameManager;

            public int camID;
            public bool useAOV;
            public bool temporal;

            public int width;
            public int height;
            public int slices;
        }

        void RenderDenoisePass(RenderGraph renderGraph, HDCamera hdCamera, TextureHandle outputTexture)
        {
#if ENABLE_UNITY_DENOISERS
            using (var builder = renderGraph.AddRenderPass<RenderDenoisePassData>("Denoise Pass", out var passData))
            {
                passData.blitAndExposeCS = m_Asset.renderPipelineResources.shaders.blitAndExposeCS;
                passData.blitAndExposeKernel = passData.blitAndExposeCS.FindKernel("KMain");
                passData.subFrameManager = m_SubFrameManager;
                passData.useAOV = m_PathTracingSettings.useAOVs.value;
                passData.temporal = m_PathTracingSettings.temporal.value && hdCamera.camera.cameraType == CameraType.Game;

                // copy camera params
                passData.camID = hdCamera.camera.GetInstanceID();
                passData.width = hdCamera.actualWidth;
                passData.height = hdCamera.actualHeight;
                passData.slices = hdCamera.viewCount;

                // Grab the history buffer
                TextureHandle history = renderGraph.ImportTexture(hdCamera.GetCurrentFrameRT((int)HDCameraFrameHistoryType.PathTracing)
                    ?? hdCamera.AllocHistoryFrameRT((int)HDCameraFrameHistoryType.PathTracing, PathTracingHistoryBufferAllocatorFunction, 1));

                passData.color = builder.WriteTexture(history);

                passData.outputTexture = builder.WriteTexture(outputTexture);

                if (m_PathTracingSettings.useAOVs.value)
                {
                    TextureHandle albedoHistory = renderGraph.ImportTexture(hdCamera.GetCurrentFrameRT((int)HDCameraFrameHistoryType.AlbedoAOV)
                        ?? hdCamera.AllocHistoryFrameRT((int)HDCameraFrameHistoryType.AlbedoAOV, PathTracingHistoryBufferAllocatorFunction, 1));

                    TextureHandle normalHistory = renderGraph.ImportTexture(hdCamera.GetCurrentFrameRT((int)HDCameraFrameHistoryType.NormalAOV)
                        ?? hdCamera.AllocHistoryFrameRT((int)HDCameraFrameHistoryType.NormalAOV, PathTracingHistoryBufferAllocatorFunction, 1));

                    passData.albedoAOV = builder.ReadTexture(albedoHistory);
                    passData.normalAOV = builder.ReadTexture(normalHistory);
                }

                if (m_PathTracingSettings.temporal.value)
                {
                    TextureHandle motionVectorHistory = renderGraph.ImportTexture(hdCamera.GetCurrentFrameRT((int)HDCameraFrameHistoryType.MotionVectorAOV)
                        ?? hdCamera.AllocHistoryFrameRT((int)HDCameraFrameHistoryType.MotionVectorAOV, PathTracingHistoryBufferAllocatorFunction, 1));
                    passData.motionVectorAOV = builder.ReadTexture(motionVectorHistory);
                }

                builder.SetRenderFunc(
                (RenderDenoisePassData data, RenderGraphContext ctx) =>
                {
                    CameraData camData = data.subFrameManager.GetCameraData(data.camID);

                    camData.denoiser.type = m_PathTracingSettings.denoiser.value;

                    if (camData.currentIteration == (data.subFrameManager.subFrameCount) && camData.denoiser.type != DenoiserType.None)
                    {
                        bool useAsync = false;

                        if (!camData.denoiser.denoised)
                        {
                            camData.denoiser.denoised = true;
                            m_SubFrameManager.SetCameraData(data.camID, camData);

                            camData.denoiser.DenoiseRequest(ctx.cmd, "color", data.color);

                            if (data.useAOV)
                            {
                                camData.denoiser.DenoiseRequest(ctx.cmd, "albedo", data.albedoAOV);
                                camData.denoiser.DenoiseRequest(ctx.cmd, "normal", data.normalAOV);
                            }

                            if (data.temporal)
                            {
                                camData.denoiser.DenoiseRequest(ctx.cmd, "flow", data.motionVectorAOV);
                            }

                            if (!useAsync)
                            {
                                camData.denoiser.WaitForCompletion();
                                camData.denoiser.GetResults(ctx.cmd, data.color);

                                // Blit the denoised image from the history buffer and apply exposure.
                                ctx.cmd.SetComputeTextureParam(data.blitAndExposeCS, data.blitAndExposeKernel, HDShaderIDs._InputTexture, data.color);
                                ctx.cmd.SetComputeTextureParam(data.blitAndExposeCS, data.blitAndExposeKernel, HDShaderIDs._OutputTexture, data.outputTexture);
                                ctx.cmd.DispatchCompute(data.blitAndExposeCS, data.blitAndExposeKernel, (data.width + 7) / 8, (data.height + 7) / 8, data.slices);
                            }
                        }

                        // if denoised frame is ready, blit it
                        if (useAsync && camData.denoiser.QueryCompletion())
                        {
                            camData.denoiser.GetResults(ctx.cmd, data.color);
                        }
                    }
                });
            }
        }
#endif
    }
}
