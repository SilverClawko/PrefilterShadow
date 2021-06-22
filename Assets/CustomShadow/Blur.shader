Shader "CustomEffect/Blur"
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

    v2f vert_base(appdata v)
    {
        v2f o;
        o.vertex = TransformObjectToHClip(v.vertex.xyz);
        o.uv = TRANSFORM_TEX(v.uv, _MainTex);
        return o;
    }

    float _BlurSize;

    // Dual Filter
    struct v2f_downSample
    {
        float2 uv : TEXCOORD0;
        float4 uv01: TEXCOORD1;
        float4 uv23: TEXCOORD2;
        float4 vertex : SV_POSITION;
    };

    v2f_downSample vert_downSample(appdata v)
    {
        v2f_downSample o;
        o.vertex = TransformObjectToHClip(v.vertex.xyz);
        float2 uv = TRANSFORM_TEX(v.uv, _MainTex);
        float2 uvOffset = _MainTex_TexelSize.xy * 0.5;
        float2 blurOffet = 1 + _BlurSize;

        o.uv = uv;
        o.uv01.xy = uv - uvOffset * blurOffet;//top right
        o.uv01.zw = uv + uvOffset * blurOffet;//bottom left
        o.uv23.xy = uv - float2(uvOffset.x, -uvOffset.y) * blurOffet;//top left
        o.uv23.zw = uv + float2(uvOffset.x, -uvOffset.y) * blurOffet;//bottom right
        return o;
    }

    float4 frag_downSample(v2f_downSample i) : SV_Target
    {
        float4 sum = tex2D(_MainTex, i.uv) * 4;
        sum += tex2D(_MainTex, i.uv01.xy);
        sum += tex2D(_MainTex, i.uv01.zw);
        sum += tex2D(_MainTex, i.uv23.xy);
        sum += tex2D(_MainTex, i.uv23.zw);

        return sum * 0.125;
    }

    struct v2f_upSample
    {
        float4 uv01: TEXCOORD0;
        float4 uv23: TEXCOORD1;
        float4 uv45: TEXCOORD2;
        float4 uv67: TEXCOORD3;
        float4 vertex : SV_POSITION;
    };

    v2f_upSample vert_upSample(appdata v)
    {
        v2f_upSample o;
        o.vertex = TransformObjectToHClip(v.vertex.xyz);
        float2 uv = TRANSFORM_TEX(v.uv, _MainTex);
        float2 uvOffset = _MainTex_TexelSize * 0.5;
        float2 blurOffset = 1 + _BlurSize;

        o.uv01.xy = uv + float2(-uvOffset.x * 2, 0) * blurOffset;
        o.uv01.zw = uv + float2(-uvOffset.x, uvOffset.y) * blurOffset;
        o.uv23.xy = uv + float2(0, uvOffset.y * 2) * blurOffset;
        o.uv23.zw = uv + uvOffset * blurOffset;
        o.uv45.xy = uv + float2(uvOffset.x * 2, 0) * blurOffset;
        o.uv45.zw = uv + float2(uvOffset.x, -uvOffset.y) * blurOffset;
        o.uv67.xy = uv + float2(0, -uvOffset.y * 2) * blurOffset;
        o.uv67.zw = uv - uvOffset * blurOffset;
        return o;
    }

    float4 frag_upSample(v2f_upSample i) : SV_Target
    {
        float4 sum = 0;
        sum += tex2D(_MainTex, i.uv01.xy);
        sum += tex2D(_MainTex, i.uv01.zw) * 2;
        sum += tex2D(_MainTex, i.uv23.xy);
        sum += tex2D(_MainTex, i.uv23.zw) * 2;
        sum += tex2D(_MainTex, i.uv45.xy);
        sum += tex2D(_MainTex, i.uv45.zw) * 2;
        sum += tex2D(_MainTex, i.uv67.xy);
        sum += tex2D(_MainTex, i.uv67.zw) * 2;

        return sum * 0.0833333;
    }

    float _KawaseOffset;

    // Kawase Filter
    float4 frag_kawase(v2f i) : SV_Target
    {
        float4 o = 0;
        o += tex2D(_MainTex, i.uv + float2(_KawaseOffset + 0.5, _KawaseOffset + 0.5) * _MainTex_TexelSize.xy);
        o += tex2D(_MainTex, i.uv + float2(-_KawaseOffset - 0.5, _KawaseOffset + 0.5) * _MainTex_TexelSize.xy);
        o += tex2D(_MainTex, i.uv + float2(-_KawaseOffset - 0.5, -_KawaseOffset - 0.5) * _MainTex_TexelSize.xy);
        o += tex2D(_MainTex, i.uv + float2(_KawaseOffset + 0.5, -_KawaseOffset - 0.5) * _MainTex_TexelSize.xy);
        return o * 0.25;
    }

    float4 frag_gaussian3x3(v2f i) : SV_Target
    {
        float4 o = 0;

        const float gussianKernel[9] = {
            0.077847, 0.123317, 0.077847,
            0.123317, 0.195346, 0.123317,
            0.077847, 0.123317, 0.077847,
        };

        float2 blurOffset = 1 + _BlurSize;

        for (int x = -1; x <= 1; ++x) {
            for (int y = -1; y <= 1; ++y) {
                float weight = gussianKernel[x * 3 + y + 4];
                o += weight * tex2D(_MainTex, i.uv + float2(x, y) * _MainTex_TexelSize.xy * blurOffset);
            }
        }

        return o;
    }

    float4 frag_gaussian5x5(v2f i) : SV_Target
    {
        float4 o = 0;

        const float gussianKernel[25] = {
            0.002969, 0.013306, 0.021938, 0.013306, 0.002969,
            0.013306, 0.059634, 0.098320, 0.059634, 0.013306,
            0.021938, 0.098320, 0.162103, 0.098320, 0.021938,
            0.013306, 0.059634, 0.098320, 0.059634, 0.013306,
            0.002969, 0.013306, 0.021938, 0.013306, 0.002969,
        };

        float2 blurOffset = 1 + _BlurSize;

        for (int x = -2; x <= 2; ++x) {
            for (int y = -2; y <= 2; ++y) {
                float weight = gussianKernel[x * 5 + y + 11];
                o += weight * tex2D(_MainTex, i.uv + float2(x, y) * _MainTex_TexelSize.xy * blurOffset);
            }
        }

        return o;
    }

    ENDHLSL

    SubShader
    {
        // Pass 0 Dual Filter DownSample
        Pass
        {
            Name "DualFilterDownSample"

            HLSLPROGRAM
            #pragma vertex vert_downSample
            #pragma fragment frag_downSample
            ENDHLSL
        }

        // Pass 1 Dual Filter UpSample
        Pass
        {
            Name "DualFilterUpSample"

            HLSLPROGRAM
            #pragma vertex vert_upSample
            #pragma fragment frag_upSample
            ENDHLSL
        }

        // Pass 2 Kawase Filter
        Pass
        {
            Name "KawaseFilter"

            HLSLPROGRAM
            #pragma vertex vert_base
            #pragma fragment frag_kawase
            ENDHLSL
        }

        // Pass 3 Gaussian3x3
        Pass
        {
            Name "Gaussian3x3"

            HLSLPROGRAM
            #pragma vertex vert_base
            #pragma fragment frag_gaussian3x3
            ENDHLSL
        }

        // Pass 4 Gaussian5x5
        Pass
        {
            Name "Gaussian5x5"

            HLSLPROGRAM
            #pragma vertex vert_base
            #pragma fragment frag_gaussian5x5
            ENDHLSL
        }
    }
}
