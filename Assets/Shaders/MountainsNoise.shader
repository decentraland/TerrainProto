Shader "Decentraland/Mountains Noise"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Scale ("Scale", Float) = 150.0
        _Octave0X ("Octave 0 X", Float) = -99974.8203
        _Octave0Y ("Octave 0 Y", Float) = -93748.3281
        _Octave1X ("Octave 1 X", Float) = -67502.3047
        _Octave1Y ("Octave 1 Y", Float) = -22190.1895
        _Octave2X ("Octave 2 X", Float) = 77881.3359
        _Octave2Y ("Octave 2 Y", Float) = -61863.875
        _MaxHeight ("Max Height", Float) = 1.45224404
        _Persistence ("Persistence", Float) = 0.338
        _Lacunarity ("Lacunarity", Float) = 2.9
        _OffsetX ("Offset X", Float) = 0.0
        _OffsetY ("Offset Y", Float) = 0.0
        _Cutoff ("Cutoff", Float) = 0.0
        _BaseValue ("Base Value", Float) = 0.0
        _MultiplyValue ("Multiply Value", Float) = 1.0
        _DivideValue ("Divide Value", Float) = 3.82
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"
            #include "Noise/noise2D.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                UNITY_FOG_COORDS(1)
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Scale;
            float _Octave0X;
            float _Octave0Y;
            float _Octave1X;
            float _Octave1Y;
            float _Octave2X;
            float _Octave2Y;
            float _MaxHeight;
            float _Persistence;
            float _Lacunarity;
            float _OffsetX;
            float _OffsetY;
            float _Cutoff;
            float _BaseValue;
            float _MultiplyValue;
            float _DivideValue;

            v2f vert (appdata v)
            {
                v2f o;

                float halfWidth = 0.5;
                float halfHeight = 0.5;

                float amplitude = 1.0;
                float frequency = 1.0;
                float noiseHeight = 0.0;

                // Octave 0
                {
                    float sampleX = (v.vertex.x - halfWidth + _Octave0X + _OffsetX) / _Scale * frequency;
                    float sampleY = (v.vertex.z - halfHeight + _Octave0Y + _OffsetY) / _Scale * frequency;

                    float noiseValue = snoise(float2(sampleX, sampleY));

                    noiseValue = (noiseValue * 2.0) - 1.0;
                    noiseHeight += noiseValue * amplitude;

                    amplitude *= _Persistence;
                    frequency *= _Lacunarity;
                }

                // Octave 1
                {
                    float sampleX = (v.vertex.x - halfWidth + _Octave1X + _OffsetX) / _Scale * frequency;
                    float sampleY = (v.vertex.z - halfHeight + _Octave1Y + _OffsetY) / _Scale * frequency;

                    float noiseValue = snoise(float2(sampleX, sampleY));

                    noiseValue = (noiseValue * 2.0) - 1.0;
                    noiseHeight += noiseValue * amplitude;

                    amplitude *= _Persistence;
                    frequency *= _Lacunarity;
                }

                // Octave 2
                {
                    float sampleX = (v.vertex.x - halfWidth + _Octave2X + _OffsetX) / _Scale * frequency;
                    float sampleY = (v.vertex.z  - halfHeight + _Octave2Y + _OffsetY) / _Scale * frequency;

                    float noiseValue = snoise(float2(sampleX, sampleY));

                    noiseValue = (noiseValue * 2.0) - 1.0;
                    noiseHeight += noiseValue * amplitude;

                    amplitude *= _Persistence;
                    frequency *= _Lacunarity;
                }

                float tempValue = noiseHeight;

                tempValue += _BaseValue;
                tempValue *= max(_MultiplyValue, 1.0);
                tempValue /= max(_DivideValue, 1.0);

                float normalizedHeight = (tempValue + 1.0) / _MaxHeight;
                tempValue = clamp(normalizedHeight, 0.0, 1.0);

                if (tempValue < _Cutoff)
                    tempValue = 0.0;

                v.vertex.y = tempValue;

                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);
                return col;
            }
            ENDCG
        }
    }
}