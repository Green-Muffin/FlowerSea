Shader "Hidden/DDGI/DebugSDF"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "DDGI Debug SDF"
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE3D(_DDGISDF);
            SAMPLER(sampler_DDGISDF);
            float4 _DDGISDFOriginSize;
            float4 _DDGISDFTexSize;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                return output;
            }

            float SampleSDF(float3 p)
            {
                float3 uvw = (p - _DDGISDFOriginSize.xyz) / (_DDGISDFOriginSize.w * _DDGISDFTexSize.xyz);
                if (any(uvw < 0.0) || any(uvw > 1.0))
                    return 1000.0;
                return SAMPLE_TEXTURE3D_LOD(_DDGISDF, sampler_DDGISDF, uvw, 0).r;
            }

            float3 SDFGradient(float3 p)
            {
                float e = _DDGISDFOriginSize.w * 0.5;
                float dx = SampleSDF(p + float3(e, 0, 0)) - SampleSDF(p - float3(e, 0, 0));
                float dy = SampleSDF(p + float3(0, e, 0)) - SampleSDF(p - float3(0, e, 0));
                float dz = SampleSDF(p + float3(0, 0, e)) - SampleSDF(p - float3(0, 0, e));
                return normalize(float3(dx, dy, dz));
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv01 = input.positionCS.xy / _ScreenSize.xy;
                float2 ndc = uv01 * 2.0 - 1.0;
                #if UNITY_UV_STARTS_AT_TOP
                ndc.y = -ndc.y;
                #endif
                float4 unprojected = mul(UNITY_MATRIX_I_VP, float4(ndc, 0.5, 1.0));
                float3 world = unprojected.xyz / unprojected.w;
                float3 rayOrigin = _WorldSpaceCameraPos.xyz;
                float3 rayDir = normalize(world - rayOrigin);

                float voxel = _DDGISDFOriginSize.w;
                float tMax = length(voxel * _DDGISDFTexSize.xyz) * 1.5;
                float t = 0.0;
                float sd = 1000.0;
                bool hit = false;

                [loop]
                for (int i = 0; i < 160; i++)
                {
                    sd = SampleSDF(rayOrigin + rayDir * t);
                    if (sd < voxel * 0.25)
                    {
                        hit = true;
                        break;
                    }
                    t += max(sd, voxel * 0.2);
                    if (t > tMax)
                        break;
                }

                float3 color;
                if (hit)
                {
                    float3 p = rayOrigin + rayDir * t;
                    float3 n = SDFGradient(p);
                    float lambert = saturate(dot(n, normalize(float3(0.4, 0.8, 0.3))));
                    color = (0.5 + 0.5 * n) * (0.3 + 0.7 * lambert);
                    if (sd < 0.0)
                        color = float3(0.8, 0.1, 0.1);
                }
                else
                {
                    color = float3(0.05, 0.07, 0.12);
                }
                return float4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
