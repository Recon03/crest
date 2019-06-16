﻿// Crest Ocean System

// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE)

using UnityEngine;
using UnityEngine.Rendering;

namespace Crest
{
    /// <summary>
    /// Stores shadowing data to use during ocean shading. Shadowing is persistent and supports sampling across
    /// many frames and jittered sampling for (very) soft shadows.
    /// </summary>
    public class LodDataMgrShadow : LodDataMgr
    {
        public override string SimName { get { return "Shadow"; } }
        public override RenderTextureFormat TextureFormat { get { return RenderTextureFormat.RG16; } }
        protected override bool NeedToReadWriteTextureData { get { return true; } }

        public static bool s_processData = true;

        Light _mainLight;
        Camera _cameraMain;

        CommandBuffer _bufCopyShadowMap = null;
        RenderTexture _sources;
        PropertyWrapperCompute[] _renderProperties;
        ComputeShader _shader;
        private int krnl_UpdateShadow;
        public const string UpdateShadow = "UpdateShadow";

        static int sp_CenterPos = Shader.PropertyToID("_CenterPos");
        static int sp_Scale = Shader.PropertyToID("_Scale");
        static int sp_CamPos = Shader.PropertyToID("_CamPos");
        static int sp_CamForward = Shader.PropertyToID("_CamForward");
        static int sp_JitterDiameters_CurrentFrameWeights = Shader.PropertyToID("_JitterDiameters_CurrentFrameWeights");
        static int sp_MainCameraProjectionMatrix = Shader.PropertyToID("_MainCameraProjectionMatrix");
        static int sp_SimDeltaTime = Shader.PropertyToID("_SimDeltaTime");
        static int sp_LD_SliceIndex_Source = Shader.PropertyToID("_LD_SliceIndex_Source");

        SimSettingsShadow Settings { get { return OceanRenderer.Instance._simSettingsShadow; } }
        public override void UseSettings(SimSettingsBase settings) { OceanRenderer.Instance._simSettingsShadow = settings as SimSettingsShadow; }
        public override SimSettingsBase CreateDefaultSettings()
        {
            var settings = ScriptableObject.CreateInstance<SimSettingsShadow>();
            settings.name = SimName + " Auto-generated Settings";
            return settings;
        }

        protected override void Start()
        {
            base.Start();

            {
                _renderProperties = new PropertyWrapperCompute[OceanRenderer.Instance.CurrentLodCount];
                _shader = Resources.Load<ComputeShader>(UpdateShadow);
                krnl_UpdateShadow = _shader.FindKernel(UpdateShadow);
                for (int i = 0; i < _renderProperties.Length; i++)
                {
                    _renderProperties[i] = new PropertyWrapperCompute();
                }
            }

            _cameraMain = Camera.main;
            if (_cameraMain == null)
            {
                var viewpoint = OceanRenderer.Instance.Viewpoint;
                _cameraMain = viewpoint != null ? viewpoint.GetComponent<Camera>() : null;

                if (_cameraMain == null)
                {
                    Debug.LogError("Could not find main camera, disabling shadow data", this);
                    enabled = false;
                    return;
                }
            }

#if UNITY_EDITOR
            if (!OceanRenderer.Instance.OceanMaterial.IsKeywordEnabled("_SHADOWS_ON"))
            {
                Debug.LogWarning("Shadowing is not enabled on the current ocean material and will not be visible.", this);
            }
#endif
        }

        protected override void InitData()
        {
            base.InitData();

            Debug.Assert(SystemInfo.SupportsRenderTextureFormat(TextureFormat), "The graphics device does not support the render texture format " + TextureFormat.ToString());

            int resolution = OceanRenderer.Instance.LodDataResolution;
            var desc = new RenderTextureDescriptor(resolution, resolution, TextureFormat, 0);

            _sources = new RenderTexture(desc);
            _sources.wrapMode = TextureWrapMode.Clamp;
            _sources.antiAliasing = 1;
            _sources.filterMode = FilterMode.Bilinear;
            _sources.anisoLevel = 0;
            _sources.useMipMap = false;
            _sources.name = SimName;
            _sources.dimension = TextureDimension.Tex2DArray;
            _sources.volumeDepth = OceanRenderer.Instance.CurrentLodCount;
            _sources.enableRandomWrite = NeedToReadWriteTextureData;
        }

        bool StartInitLight()
        {
            _mainLight = OceanRenderer.Instance._primaryLight;

            if (_mainLight.type != LightType.Directional)
            {
                Debug.LogError("Primary light must be of type Directional.", this);
                return false;
            }

            if (_mainLight.shadows == LightShadows.None)
            {
                Debug.LogError("Shadows must be enabled on primary light to enable ocean shadowing (types Hard and Soft are equivalent for the ocean system).", this);
                return false;
            }

            return true;
        }

        public override void UpdateLodData()
        {
            base.UpdateLodData();

            if (_mainLight != OceanRenderer.Instance._primaryLight)
            {
                if (_mainLight)
                {
                    _mainLight.RemoveCommandBuffer(LightEvent.BeforeScreenspaceMask, _bufCopyShadowMap);
                    _bufCopyShadowMap = null;
                    for(int lodIdx = 0; lodIdx < _targets.volumeDepth; lodIdx++)
                    {
                        Graphics.Blit(Texture2D.blackTexture, _sources, -1, lodIdx);
                        Graphics.Blit(Texture2D.blackTexture, _targets, -1, lodIdx);
                    }
                }
                _mainLight = null;
            }

            if (!OceanRenderer.Instance._primaryLight)
            {
                if (!Settings._allowNullLight)
                {
                    Debug.LogWarning("Primary light must be specified on OceanRenderer script to enable shadows.", this);
                }
                return;
            }

            if (!_mainLight)
            {
                if (!StartInitLight())
                {
                    enabled = false;
                    return;
                }
            }

            if (_bufCopyShadowMap == null && s_processData)
            {
                _bufCopyShadowMap = new CommandBuffer();
                _bufCopyShadowMap.name = "Shadow data";
                _mainLight.AddCommandBuffer(LightEvent.BeforeScreenspaceMask, _bufCopyShadowMap);
            }
            else if (!s_processData && _bufCopyShadowMap != null)
            {
                _mainLight.RemoveCommandBuffer(LightEvent.BeforeScreenspaceMask, _bufCopyShadowMap);
                _bufCopyShadowMap = null;
            }

            if (!s_processData)
                return;


            var lodCount = OceanRenderer.Instance.CurrentLodCount;

            SwapRTs(ref _sources, ref _targets);

            _bufCopyShadowMap.Clear();

            var lt = OceanRenderer.Instance._lodTransform;
            ValidateSourceData();
            for (var lodIdx = OceanRenderer.Instance.CurrentLodCount - 1; lodIdx >= 0; lodIdx--)
            {
                // clear the shadow collection. it will be overwritten with shadow values IF the shadows render,
                // which only happens if there are (nontransparent) shadow receivers around
                Graphics.Blit(Texture2D.blackTexture, _targets, -1, lodIdx);

                _renderProperties[lodIdx].Initialise(_bufCopyShadowMap, _shader, krnl_UpdateShadow);

                lt._renderData[lodIdx].Validate(0, this);
                _renderProperties[lodIdx].SetVector(sp_CenterPos, lt._renderData[lodIdx]._posSnapped);
                _renderProperties[lodIdx].SetVector(sp_Scale, lt.GetLodTransform(lodIdx).lossyScale);
                _renderProperties[lodIdx].SetVector(sp_CamPos, OceanRenderer.Instance.Viewpoint.position);
                _renderProperties[lodIdx].SetVector(sp_CamForward, OceanRenderer.Instance.Viewpoint.forward);
                _renderProperties[lodIdx].SetVector(sp_JitterDiameters_CurrentFrameWeights, new Vector4(Settings._jitterDiameterSoft, Settings._jitterDiameterHard, Settings._currentFrameWeightSoft, Settings._currentFrameWeightHard));
                _renderProperties[lodIdx].SetMatrix(sp_MainCameraProjectionMatrix, _cameraMain.projectionMatrix * _cameraMain.worldToCameraMatrix);
                _renderProperties[lodIdx].SetFloat(sp_SimDeltaTime, Time.deltaTime);

                // compute which lod data we are sampling previous frame shadows from. if a scale change has happened this can be any lod up or down the chain.
                var srcDataIdx = lodIdx + ScaleDifferencePow2;
                srcDataIdx = Mathf.Clamp(srcDataIdx, 0, lt.LodCount - 1);
                _renderProperties[lodIdx].SetFloat(OceanRenderer.sp_LD_SliceIndex, lodIdx);
                _renderProperties[lodIdx].SetFloat(sp_LD_SliceIndex_Source, srcDataIdx);
                BindSourceData(_renderProperties[lodIdx], false);
                _renderProperties[lodIdx].SetTexture(
                    Shader.PropertyToID("_LD_TexArray_Target"),
                    _targets
                );
                _renderProperties[lodIdx].DispatchShader();

                //_bufCopyShadowMap.Blit(Texture2D.blackTexture, _targets, _renderProperties[lodIdx].material, -1, lodIdx);
            }
        }

        public void ValidateSourceData()
        {
            foreach(var renderData in  OceanRenderer.Instance._lodTransform._renderDataSource)
            {
                renderData.Validate(BuildCommandBufferBase._lastUpdateFrame - Time.frameCount, this);
            }
        }

        public void BindSourceData(IPropertyWrapper simMaterial, bool paramsOnly)
        {
            var rd = OceanRenderer.Instance._lodTransform._renderDataSource;
            BindData(simMaterial, paramsOnly ? Texture2D.blackTexture : _sources as Texture, true, ref rd, true);
        }

        void OnEnable()
        {
            RemoveCommandBuffers();
        }

        void OnDisable()
        {
            RemoveCommandBuffers();
        }

        void RemoveCommandBuffers()
        {
            if (_bufCopyShadowMap != null)
            {
                if (_mainLight)
                {
                    _mainLight.RemoveCommandBuffer(LightEvent.BeforeScreenspaceMask, _bufCopyShadowMap);
                }
                _bufCopyShadowMap = null;
            }
        }

        public static string TextureArrayName = "_LD_TexArray_Shadow";
        private static TextureArrayParamIds textureArrayParamIds = new TextureArrayParamIds(TextureArrayName);
        public static int ParamIdSampler(bool sourceLod = false) { return textureArrayParamIds.GetId(sourceLod); }
        protected override int GetParamIdSampler(bool sourceLod = false)
        {
            return ParamIdSampler(sourceLod);
        }
        public static void BindNull(IPropertyWrapper properties, bool sourceLod = false)
        {
            properties.SetTexture(ParamIdSampler(sourceLod), TextureArrayHelpers.BlackTextureArray);
        }
    }
}
