Shader "CustomEffect/CustomShadow"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

    sampler2D _MainTex;
    float4 _MainTex_ST;
    float4 _MainTex_TexelSize;

    float _ESMConst;

    float _VSMMin;

    float _EVSMConstX;
    float _EVSMConstY;

    struct appdata
    {
        float4 vertex : POSITION;
        float2 uv : TEXCOORD0;
    };

    struct v2f
    {
        float2 uv : TEXCOORD0;
        float4 vertex : SV_POSITION;
    };

    v2f vert(appdata v)
    {
        v2f o;
        o.vertex = TransformObjectToHClip(v.vertex.xyz);
        o.uv = TRANSFORM_TEX(v.uv, _MainTex);
        return o;
    }

    ENDHLSL

    SubShader
    {    
        // Pass 0 ESM 
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(v2f i) : SV_Target
            {
                // log space prefilter
                //const float gussianKernel[9] = {
                //    0.077847, 0.123317, 0.077847,
                //    0.123317, 0.195346, 0.123317,
                //    0.077847, 0.123317, 0.077847,
                //};

                //float2 uvOffset = _MainTex_TexelSize.xy;

                //float d0 = tex2D(_MainTex, i.uv).r;
                //float other = gussianKernel[4];

                //for (int x = -1; x <= 1; ++x) {
                //    for (int y = -1; y <= 1; ++y) {

                //        if (x == 0 && y == 0)
                //            continue;

                //        float d = tex2D(_MainTex, i.uv + float2(x, y) * uvOffset).r;
                //        float weight = gussianKernel[x * 3 + y + 4];
                //        other += weight * exp(_ESMConst * (d - d0));
                //    }
                //}

                //float sum = _ESMConst * d0 + log(other);
                //return float4(sum, 0, 0, 0);

                float d = tex2D(_MainTex, i.uv).r;
            #if UNITY_REVERSED_Z
                float e = exp(-_ESMConst * d);
            #else
                float e = exp(_ESMConst * d);
            #endif
                return float4(e, 0, 0, 0);
            }

            ENDHLSL
        }

        // Pass 1 VSM
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(v2f i) : SV_Target
            {
                float ex = tex2D(_MainTex, i.uv).r;
                float ex2 = ex * ex;
                return float4(ex, ex2, 0, 0);
            }

            ENDHLSL
        }

        // Pass 2 EVSM Four Component
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(v2f i) : SV_Target
            {
                float d = tex2D(_MainTex, i.uv).r;
                float positive = exp(_EVSMConstX * d);
                float negative = -exp(-_EVSMConstY * d);
                return float4(positive, positive * positive, negative, negative * negative);
            }

            ENDHLSL
        }

        // Pass 4 DownSample
        UsePass "CustomEffect/Blur/DualFilterDownSample"

        // Pass 5 UpSample
        UsePass "CustomEffect/Blur/DualFilterUpSample"

        // Pass 6 Kawase Filter
        UsePass "CustomEffect/Blur/KawaseFilter"

        // Pass 7 Gaussian3x3
        UsePass "CustomEffect/Blur/Gaussian3x3"

        // Pass 8 Gaussian5x5
        UsePass "CustomEffect/Blur/Gaussian5x5"
    }
}
