Shader "Custom/OccupancyMapDisplay"
{
    Properties
    {
        _OccupancyMap ("Occupancy Map", 2D) = "white" {}
        _BlackColor ("Black Color (Empty)", Color) = (0, 0, 0, 1)
        _WhiteColor ("White Color (Occupied)", Color) = (1, 1, 1, 1)
        _CenterX ("Center X", Float) = 263
        _CenterY ("Center Y", Float) = 260  
        _MaxRadius ("Max Radius", Float) = 170
        _ParcelSize ("Parcel Size", Float) = 16
        _BlackAlpha ("Black Alpha (Transparency)", Range(0, 1)) = 1
    }
    
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 100
        
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            #include "UnityCG.cginc"
            
            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };
            
            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float3 worldPos : TEXCOORD1;
            };
            
            sampler2D _OccupancyMap;
            float4 _OccupancyMap_ST;
            float4 _BlackColor;
            float4 _WhiteColor;
            float _CenterX;
            float _CenterY;
            float _MaxRadius;
            float _ParcelSize;
            float _BlackAlpha;
            
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _OccupancyMap);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }
            
            fixed4 frag (v2f i) : SV_Target
            {
                // Вычисляем парсел координаты от мировой позиции
                float parcelX = i.worldPos.x / _ParcelSize;
                float parcelY = i.worldPos.z / _ParcelSize;
                
                // Проверяем радиус от центра (0,0)
                float distance = sqrt(parcelX * parcelX + parcelY * parcelY);
                if (distance > _MaxRadius)
                {
                    discard; // Обрезаем за пределами радиуса
                }
                
                // Получаем размеры текстуры
                float2 texSize = float2(512, 512); // Предполагаем 512x512
                
                // Преобразуем парсел координаты в UV координаты текстуры
                float pixelX = _CenterX + parcelX;
                float pixelY = _CenterY + parcelY;
                
                float2 texCoords = float2(pixelX / texSize.x, pixelY / texSize.y);
                
                // Проверяем границы текстуры
                if (texCoords.x < 0 || texCoords.x > 1 || texCoords.y < 0 || texCoords.y > 1)
                {
                    discard;
                }
                
                // Сэмплируем текстуру
                fixed4 texColor = tex2D(_OccupancyMap, texCoords);
                
                // Делаем четкий threshold (>0.5 = белый, <=0.5 = черный)
                float threshold = 0.5;
                bool isOccupied = texColor.r > threshold;
                
                fixed4 finalColor = isOccupied ? _WhiteColor : _BlackColor;
                
                // Применяем настраиваемую прозрачность к черным участкам
                if (!isOccupied)
                {
                    finalColor.a = _BlackAlpha;
                }
                
                return finalColor;
            }
            ENDCG
        }
    }
}
