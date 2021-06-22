using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;



public class CustomShadowFeature : ScriptableRendererFeature
{
	public enum ShadowType
	{
		Default,
		ESM,
		VSM,
		EVSM,
	}

	public enum ShadowPrefilterType
	{
		Default,
		Kawase,
		Dual,
		Gaussian3x3,
		Gaussian5x5,
	}

	[System.Serializable]
	public class CustomShadowSettings
	{
		public RenderPassEvent evt = RenderPassEvent.BeforeRenderingShadows;
		public Material shadowMaterial = null;

		public ShadowType shadowType = ShadowType.Default;
		public ShadowPrefilterType filterType = ShadowPrefilterType.Default;

		[Range(0f, 5f)] public float blurSize = 0.5f;

		[Range(1, 10)] public int kawaseFilterCount = 6;
		[Range(1, 8)] public float kawaseDownSampleScale = 2;

		[Range(1, 4)] public int dualFilterCount = 2;

		public bool useHalfDepth = false;

		[Range(0, 100)] public float esmConst = 80.0f;

		[Range(0, 1)] public float vsmMin = 0.5f;

		[Range(0, 100)] public float evsmConstX = 40.0f;
		[Range(0, 100)] public float evsmConstY = 5.0f;
		[Range(0, 1)] public float evsmMin = 0.1f;
	}

	public class CustomShadowPass : ScriptableRenderPass
	{
		private static class CustomShadowUniform
		{
			public static int ESMConstID;
			public static int VSMMinID;
			public static int EVSMConstXID;
			public static int EVSMConstYID;
			public static int EVSMMinID;

			public static int BlurSizeID;
			public static int KawaseOffsetID;
		}

		private static class MainLightShadowConstantBuffer
		{
			public static int _WorldToShadow;
			public static int _ShadowParams;
			public static int _CascadeShadowSplitSpheres0;
			public static int _CascadeShadowSplitSpheres1;
			public static int _CascadeShadowSplitSpheres2;
			public static int _CascadeShadowSplitSpheres3;
			public static int _CascadeShadowSplitSphereRadii;
			public static int _ShadowOffset0;
			public static int _ShadowOffset1;
			public static int _ShadowOffset2;
			public static int _ShadowOffset3;
			public static int _ShadowmapSize;
		}

		const int k_MaxCascades = 4;
		const int k_ShadowmapBufferBits = 32;
		int m_ShadowmapWidth;
		int m_ShadowmapHeight;
		int m_ShadowCasterCascadesCount;
		bool m_SupportsBoxFilterForShadows;

		CustomShadowSettings m_Settings;

		RenderTexture m_MainLightShadowmapTexture;
		RenderTargetHandle m_MainLightTemporaryShadowmap;
		RenderTargetHandle[] m_TemporaryRTs = new RenderTargetHandle[4];

		Matrix4x4[] m_MainLightShadowMatrices;
		ShadowSliceData[] m_CascadeSlices;
		Vector4[] m_CascadeSplitDistances;

		const string m_ProfilerTag = "Render Custom Main Light Shadowmap";
		ProfilingSampler m_ProfilingSampler = new ProfilingSampler(m_ProfilerTag);

		public CustomShadowPass(CustomShadowSettings settings)
		{
			m_Settings = settings;

			renderPassEvent = m_Settings.evt;

			m_MainLightShadowMatrices = new Matrix4x4[k_MaxCascades + 1];
			m_CascadeSlices = new ShadowSliceData[k_MaxCascades];
			m_CascadeSplitDistances = new Vector4[k_MaxCascades];

			MainLightShadowConstantBuffer._WorldToShadow = Shader.PropertyToID("_MainLightWorldToShadow");
			MainLightShadowConstantBuffer._ShadowParams = Shader.PropertyToID("_MainLightShadowParams");
			MainLightShadowConstantBuffer._CascadeShadowSplitSpheres0 = Shader.PropertyToID("_CascadeShadowSplitSpheres0");
			MainLightShadowConstantBuffer._CascadeShadowSplitSpheres1 = Shader.PropertyToID("_CascadeShadowSplitSpheres1");
			MainLightShadowConstantBuffer._CascadeShadowSplitSpheres2 = Shader.PropertyToID("_CascadeShadowSplitSpheres2");
			MainLightShadowConstantBuffer._CascadeShadowSplitSpheres3 = Shader.PropertyToID("_CascadeShadowSplitSpheres3");
			MainLightShadowConstantBuffer._CascadeShadowSplitSphereRadii = Shader.PropertyToID("_CascadeShadowSplitSphereRadii");
			MainLightShadowConstantBuffer._ShadowOffset0 = Shader.PropertyToID("_MainLightShadowOffset0");
			MainLightShadowConstantBuffer._ShadowOffset1 = Shader.PropertyToID("_MainLightShadowOffset1");
			MainLightShadowConstantBuffer._ShadowOffset2 = Shader.PropertyToID("_MainLightShadowOffset2");
			MainLightShadowConstantBuffer._ShadowOffset3 = Shader.PropertyToID("_MainLightShadowOffset3");
			MainLightShadowConstantBuffer._ShadowmapSize = Shader.PropertyToID("_MainLightShadowmapSize");

			m_SupportsBoxFilterForShadows = Application.isMobilePlatform || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Switch;

			m_MainLightTemporaryShadowmap.Init("_MainLightTemporaryShadowmapTextureEX");

			for (int i = 0; i < m_TemporaryRTs.Length; ++i)
			{
				m_TemporaryRTs[i].Init("TemporaryShadowRT" + i);
			}

			CustomShadowUniform.ESMConstID = Shader.PropertyToID("_ESMConst");
			CustomShadowUniform.VSMMinID = Shader.PropertyToID("_VSMMin");
			CustomShadowUniform.EVSMConstXID = Shader.PropertyToID("_EVSMConstX");
			CustomShadowUniform.EVSMConstYID = Shader.PropertyToID("_EVSMConstY");
			CustomShadowUniform.EVSMMinID = Shader.PropertyToID("_EVSMMin");
			CustomShadowUniform.BlurSizeID = Shader.PropertyToID("_BlurSize");
			CustomShadowUniform.KawaseOffsetID = Shader.PropertyToID("_KawaseOffset");
		}

		public bool Setup(ref RenderingData renderingData)
		{
			if (!renderingData.shadowData.supportsMainLightShadows)
				return false;

			Clear();
			int shadowLightIndex = renderingData.lightData.mainLightIndex;
			if (shadowLightIndex == -1)
				return false;

			VisibleLight shadowLight = renderingData.lightData.visibleLights[shadowLightIndex];
			Light light = shadowLight.light;
			if (light.shadows == LightShadows.None)
				return false;

			if (shadowLight.lightType != LightType.Directional)
			{
				Debug.LogWarning("Only directional lights are supported as main light.");
			}

			Bounds bounds;
			if (!renderingData.cullResults.GetShadowCasterBounds(shadowLightIndex, out bounds))
				return false;

			m_ShadowCasterCascadesCount = renderingData.shadowData.mainLightShadowCascadesCount;

			int shadowResolution = ShadowUtils.GetMaxTileResolutionInAtlas(renderingData.shadowData.mainLightShadowmapWidth,
				renderingData.shadowData.mainLightShadowmapHeight, m_ShadowCasterCascadesCount);
			m_ShadowmapWidth = renderingData.shadowData.mainLightShadowmapWidth;
			m_ShadowmapHeight = (m_ShadowCasterCascadesCount == 2) ?
				renderingData.shadowData.mainLightShadowmapHeight >> 1 :
				renderingData.shadowData.mainLightShadowmapHeight;

			for (int cascadeIndex = 0; cascadeIndex < m_ShadowCasterCascadesCount; ++cascadeIndex)
			{
				bool success = ShadowUtils.ExtractDirectionalLightMatrix(ref renderingData.cullResults, ref renderingData.shadowData,
					shadowLightIndex, cascadeIndex, m_ShadowmapWidth, m_ShadowmapHeight, shadowResolution, light.shadowNearPlane,
					out m_CascadeSplitDistances[cascadeIndex], out m_CascadeSlices[cascadeIndex], out m_CascadeSlices[cascadeIndex].viewMatrix, out m_CascadeSlices[cascadeIndex].projectionMatrix);

				if (!success)
					return false;
			}

			return true;
		}

		public override void Configure(CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor)
		{
			m_MainLightShadowmapTexture = ShadowUtils.GetTemporaryShadowTexture(m_ShadowmapWidth,
				m_ShadowmapHeight, k_ShadowmapBufferBits);
			ConfigureTarget(new RenderTargetIdentifier(m_MainLightShadowmapTexture));
			ConfigureClear(ClearFlag.All, Color.black);

			RenderTextureDescriptor descriptor = m_MainLightShadowmapTexture.descriptor;
			descriptor.enableRandomWrite = true;
			descriptor.depthBufferBits = 0;
			if(m_Settings.useHalfDepth)
				descriptor.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
			else
				descriptor.graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat;

			switch (m_Settings.shadowType)
			{
				case ShadowType.ESM:
					cmd.EnableShaderKeyword("_ESM");
					cmd.DisableShaderKeyword("_VSM");
					cmd.DisableShaderKeyword("_EVSM");
					cmd.GetTemporaryRT(m_MainLightTemporaryShadowmap.id, descriptor);
					break;
				case ShadowType.VSM:
					cmd.EnableShaderKeyword("_VSM");
					cmd.DisableShaderKeyword("_ESM");
					cmd.DisableShaderKeyword("_EVSM");
					cmd.GetTemporaryRT(m_MainLightTemporaryShadowmap.id, descriptor);
					break;
				case ShadowType.EVSM:
					cmd.EnableShaderKeyword("_EVSM");
					cmd.DisableShaderKeyword("_ESM");
					cmd.DisableShaderKeyword("_VSM");
					cmd.GetTemporaryRT(m_MainLightTemporaryShadowmap.id, descriptor);
					break;
				default:
					cmd.DisableShaderKeyword("_ESM");
					cmd.DisableShaderKeyword("_VSM");
					cmd.DisableShaderKeyword("_EVSM");
					break;
			}

			switch (m_Settings.filterType)
			{
				case ShadowPrefilterType.Dual:

					for (int i = 0; i < m_Settings.dualFilterCount; ++i)
					{
						RenderTextureDescriptor prefilterDescriptor = descriptor;
						prefilterDescriptor.width /= (i * 2 + 2);
						prefilterDescriptor.height /= (i * 2 + 2);

						cmd.GetTemporaryRT(m_TemporaryRTs[i].id, prefilterDescriptor);
					}
					break;

				case ShadowPrefilterType.Kawase:
					cmd.GetTemporaryRT(m_TemporaryRTs[0].id, descriptor);
					cmd.GetTemporaryRT(m_TemporaryRTs[1].id, descriptor);
					break;
				case ShadowPrefilterType.Gaussian3x3:
				case ShadowPrefilterType.Gaussian5x5:
					cmd.GetTemporaryRT(m_TemporaryRTs[0].id, descriptor);
					break;
			}
		}

		/// <inheritdoc/>
		public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
		{
			RenderMainLightExponentialShadowmap(ref context, ref renderingData.cullResults, ref renderingData.lightData, ref renderingData.shadowData);
		}

		/// <inheritdoc/>
		public override void FrameCleanup(CommandBuffer cmd)
		{
			if (cmd == null)
				throw new ArgumentNullException("cmd");

			if (m_MainLightShadowmapTexture)
			{
				RenderTexture.ReleaseTemporary(m_MainLightShadowmapTexture);
				m_MainLightShadowmapTexture = null;
			}

			if (m_Settings.shadowType > ShadowType.Default)
			{
				cmd.ReleaseTemporaryRT(m_MainLightTemporaryShadowmap.id);
			}

			switch (m_Settings.filterType)
			{
				case ShadowPrefilterType.Dual:
					for (int i = 0; i < m_Settings.dualFilterCount; ++i)
					{
						cmd.ReleaseTemporaryRT(m_TemporaryRTs[i].id);
					}
					break;
				case ShadowPrefilterType.Kawase:
					cmd.ReleaseTemporaryRT(m_TemporaryRTs[0].id);
					cmd.ReleaseTemporaryRT(m_TemporaryRTs[1].id);
					break;
				case ShadowPrefilterType.Gaussian3x3:
				case ShadowPrefilterType.Gaussian5x5:
					cmd.ReleaseTemporaryRT(m_TemporaryRTs[0].id);
					break;
			}
		}

		void Clear()
		{
			m_MainLightShadowmapTexture = null;

			for (int i = 0; i < m_MainLightShadowMatrices.Length; ++i)
				m_MainLightShadowMatrices[i] = Matrix4x4.identity;

			for (int i = 0; i < m_CascadeSplitDistances.Length; ++i)
				m_CascadeSplitDistances[i] = new Vector4(0.0f, 0.0f, 0.0f, 0.0f);

			for (int i = 0; i < m_CascadeSlices.Length; ++i)
				m_CascadeSlices[i].Clear();
		}

		void RenderMainLightExponentialShadowmap(ref ScriptableRenderContext context, ref CullingResults cullResults, ref LightData lightData, ref ShadowData shadowData)
		{
			int shadowLightIndex = lightData.mainLightIndex;
			if (shadowLightIndex == -1)
				return;

			VisibleLight shadowLight = lightData.visibleLights[shadowLightIndex];

			CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);
			using (new ProfilingScope(cmd, m_ProfilingSampler))
			{
				var settings = new ShadowDrawingSettings(cullResults, shadowLightIndex);

				for (int cascadeIndex = 0; cascadeIndex < m_ShadowCasterCascadesCount; ++cascadeIndex)
				{
					var splitData = settings.splitData;
					splitData.cullingSphere = m_CascadeSplitDistances[cascadeIndex];
					settings.splitData = splitData;
					Vector4 shadowBias = ShadowUtils.GetShadowBias(ref shadowLight, shadowLightIndex, ref shadowData, m_CascadeSlices[cascadeIndex].projectionMatrix, m_CascadeSlices[cascadeIndex].resolution);
					ShadowUtils.SetupShadowCasterConstantBuffer(cmd, ref shadowLight, shadowBias);
					ShadowUtils.RenderShadowSlice(cmd, ref context, ref m_CascadeSlices[cascadeIndex],
						ref settings, m_CascadeSlices[cascadeIndex].projectionMatrix, m_CascadeSlices[cascadeIndex].viewMatrix);
				}

				switch (m_Settings.shadowType)
				{
					case ShadowType.ESM:
						cmd.SetGlobalFloat(CustomShadowUniform.ESMConstID, m_Settings.esmConst);
						cmd.Blit(m_MainLightShadowmapTexture, m_MainLightTemporaryShadowmap.Identifier(), m_Settings.shadowMaterial, 0);
						break;
					case ShadowType.VSM:
						cmd.SetGlobalFloat(CustomShadowUniform.VSMMinID, m_Settings.vsmMin);
						cmd.Blit(m_MainLightShadowmapTexture, m_MainLightTemporaryShadowmap.Identifier(), m_Settings.shadowMaterial, 1);
						break;
					case ShadowType.EVSM:
						cmd.SetGlobalFloat(CustomShadowUniform.EVSMConstXID, m_Settings.evsmConstX);
						cmd.SetGlobalFloat(CustomShadowUniform.EVSMConstYID, m_Settings.evsmConstY);
						cmd.SetGlobalFloat(CustomShadowUniform.EVSMMinID, m_Settings.evsmMin);
						cmd.Blit(m_MainLightShadowmapTexture, m_MainLightTemporaryShadowmap.Identifier(), m_Settings.shadowMaterial, 2);	
						break;
					default:
						break;
				}

				cmd.SetGlobalFloat(CustomShadowUniform.BlurSizeID, m_Settings.blurSize);

				switch (m_Settings.filterType)
				{
					case ShadowPrefilterType.Dual:

						cmd.Blit(m_MainLightTemporaryShadowmap.Identifier(), m_TemporaryRTs[0].Identifier(), m_Settings.shadowMaterial, 3);

						for (int i = 0; i < m_Settings.dualFilterCount - 1; ++i)
						{
							cmd.Blit(m_TemporaryRTs[i].Identifier(), m_TemporaryRTs[i + 1].Identifier(), m_Settings.shadowMaterial, 3);
						}

						for (int i = m_Settings.dualFilterCount - 1; i > 0; --i)
						{
							cmd.Blit(m_TemporaryRTs[i].Identifier(), m_TemporaryRTs[i - 1].Identifier(), m_Settings.shadowMaterial, 4);
						}

						cmd.Blit(m_TemporaryRTs[0].Identifier(), m_MainLightTemporaryShadowmap.Identifier(), m_Settings.shadowMaterial, 4);

						break;
					case ShadowPrefilterType.Kawase:

						cmd.SetGlobalFloat(CustomShadowUniform.KawaseOffsetID, m_Settings.blurSize);
						cmd.Blit(m_MainLightTemporaryShadowmap.Identifier(), m_TemporaryRTs[0].Identifier(), m_Settings.shadowMaterial, 5);

						bool needSwitch = true;

						for (int i = 1; i < m_Settings.kawaseFilterCount; ++i)
						{
							cmd.SetGlobalFloat(CustomShadowUniform.KawaseOffsetID, i / m_Settings.kawaseDownSampleScale + m_Settings.blurSize);
							cmd.Blit(needSwitch ? m_TemporaryRTs[0].Identifier() : m_TemporaryRTs[1].Identifier(), needSwitch ? m_TemporaryRTs[1].Identifier() : m_TemporaryRTs[0].Identifier(), m_Settings.shadowMaterial, 5);
							needSwitch = !needSwitch;
						}
	
						cmd.Blit(needSwitch ? m_TemporaryRTs[0].Identifier() : m_TemporaryRTs[1].Identifier(), m_MainLightTemporaryShadowmap.Identifier());
						break;
					case ShadowPrefilterType.Gaussian3x3:
						cmd.Blit(m_MainLightTemporaryShadowmap.Identifier(), m_TemporaryRTs[0].Identifier(), m_Settings.shadowMaterial, 6);
						cmd.Blit(m_TemporaryRTs[0].Identifier(), m_MainLightTemporaryShadowmap.Identifier(), m_Settings.shadowMaterial);
						break;
					case ShadowPrefilterType.Gaussian5x5:
						cmd.Blit(m_MainLightTemporaryShadowmap.Identifier(), m_TemporaryRTs[0].Identifier(), m_Settings.shadowMaterial, 7);
						cmd.Blit(m_TemporaryRTs[0].Identifier(), m_MainLightTemporaryShadowmap.Identifier(), m_Settings.shadowMaterial);
						break;
				}

				bool softShadows = shadowLight.light.shadows == LightShadows.Soft && shadowData.supportsSoftShadows;
				CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.MainLightShadows, true);
				CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.MainLightShadowCascades, shadowData.mainLightShadowCascadesCount > 1);
				CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.SoftShadows, softShadows);

				SetupMainLightShadowReceiverConstants(cmd, shadowLight, softShadows);
			}

			context.ExecuteCommandBuffer(cmd);
			CommandBufferPool.Release(cmd);
		}

		void SetupMainLightShadowReceiverConstants(CommandBuffer cmd, VisibleLight shadowLight, bool softShadows)
		{
			Light light = shadowLight.light;

			int cascadeCount = m_ShadowCasterCascadesCount;
			for (int i = 0; i < cascadeCount; ++i)
				m_MainLightShadowMatrices[i] = m_CascadeSlices[i].shadowTransform;

			// We setup and additional a no-op WorldToShadow matrix in the last index
			// because the ComputeCascadeIndex function in Shadows.hlsl can return an index
			// out of bounds. (position not inside any cascade) and we want to avoid branching
			Matrix4x4 noOpShadowMatrix = Matrix4x4.zero;
			noOpShadowMatrix.m22 = (SystemInfo.usesReversedZBuffer) ? 1.0f : 0.0f;
			for (int i = cascadeCount; i <= k_MaxCascades; ++i)
				m_MainLightShadowMatrices[i] = noOpShadowMatrix;

			float invShadowAtlasWidth = 1.0f / m_ShadowmapWidth;
			float invShadowAtlasHeight = 1.0f / m_ShadowmapHeight;
			float invHalfShadowAtlasWidth = 0.5f * invShadowAtlasWidth;
			float invHalfShadowAtlasHeight = 0.5f * invShadowAtlasHeight;
			float softShadowsProp = softShadows ? 1.0f : 0.0f;

			if(m_Settings.shadowType > ShadowType.Default)
				cmd.SetGlobalTexture("_MainLightShadowmapTextureEX", m_MainLightTemporaryShadowmap.id);
	
			cmd.SetGlobalTexture("_MainLightShadowmapTexture", m_MainLightShadowmapTexture);
			cmd.SetGlobalMatrixArray(MainLightShadowConstantBuffer._WorldToShadow, m_MainLightShadowMatrices);
			cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowParams, new Vector4(light.shadowStrength, softShadowsProp, 0.0f, 0.0f));

			if (m_ShadowCasterCascadesCount > 1)
			{
				cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSpheres0,
					m_CascadeSplitDistances[0]);
				cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSpheres1,
					m_CascadeSplitDistances[1]);
				cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSpheres2,
					m_CascadeSplitDistances[2]);
				cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSpheres3,
					m_CascadeSplitDistances[3]);
				cmd.SetGlobalVector(MainLightShadowConstantBuffer._CascadeShadowSplitSphereRadii, new Vector4(
					m_CascadeSplitDistances[0].w * m_CascadeSplitDistances[0].w,
					m_CascadeSplitDistances[1].w * m_CascadeSplitDistances[1].w,
					m_CascadeSplitDistances[2].w * m_CascadeSplitDistances[2].w,
					m_CascadeSplitDistances[3].w * m_CascadeSplitDistances[3].w));
			}

			if (softShadows)
			{
				if (m_SupportsBoxFilterForShadows)
				{
					cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowOffset0,
						new Vector4(-invHalfShadowAtlasWidth, -invHalfShadowAtlasHeight, 0.0f, 0.0f));
					cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowOffset1,
						new Vector4(invHalfShadowAtlasWidth, -invHalfShadowAtlasHeight, 0.0f, 0.0f));
					cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowOffset2,
						new Vector4(-invHalfShadowAtlasWidth, invHalfShadowAtlasHeight, 0.0f, 0.0f));
					cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowOffset3,
						new Vector4(invHalfShadowAtlasWidth, invHalfShadowAtlasHeight, 0.0f, 0.0f));
				}

				// Currently only used when !SHADER_API_MOBILE but risky to not set them as it's generic
				// enough so custom shaders might use it.
				cmd.SetGlobalVector(MainLightShadowConstantBuffer._ShadowmapSize, new Vector4(invShadowAtlasWidth,
					invShadowAtlasHeight,
					m_ShadowmapWidth, m_ShadowmapHeight));
			}
		}
	};

	public CustomShadowSettings m_Settings = new CustomShadowSettings();

    CustomShadowPass m_ScriptablePass;

	public override void Create()
    {
		m_ScriptablePass = new CustomShadowPass(m_Settings);//, m_ESMCS, m_VSMCS, m_EVSMCS);
	}

    // Here you can inject one or multiple render passes in the renderer.
    // This method is called when setting up the renderer once per-camera.
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_ScriptablePass.Setup(ref renderingData))
        {
			renderer.EnqueuePass(m_ScriptablePass);
        }
    }
}


