// 交互水波用的场景高度：俯视正交投影，输出世界高度，深度测试保留最高点
// 投影矩阵由 PLOceanRipple 拼好（_PLOceanRippleHeightVP），不依赖相机
Shader "Hidden/PLOcean/RippleSceneHeight"
{
    SubShader
    {
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            float4x4 unity_ObjectToWorld;
            float4x4 _PLOceanRippleHeightVP;
            // HDRP 的相机相对渲染：DrawRenderer 走普通管线状态，ObjectToWorld 是绝对坐标

            struct Attributes { float3 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float height : TEXCOORD0; };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                float3 positionWS = mul(unity_ObjectToWorld, float4(input.positionOS, 1.0)).xyz;
                o.positionCS = mul(_PLOceanRippleHeightVP, float4(positionWS, 1.0));
                o.height = positionWS.y;
                return o;
            }

            float Frag(Varyings input) : SV_Target
            {
                return input.height;
            }
            ENDHLSL
        }
    }
}
