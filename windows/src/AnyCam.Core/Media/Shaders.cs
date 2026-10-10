namespace AnyCam.Core.Media;

/// <summary>HLSL for the processing pipeline (compiled at startup with D3DCompiler).</summary>
internal static class Shaders
{
    public const string Source = """
        struct VSOut
        {
            float4 Position : SV_Position;
            float2 Uv : TEXCOORD0;
        };

        // Full-screen triangle, no vertex buffer.
        VSOut VSFullscreen(uint id : SV_VertexID)
        {
            VSOut output;
            float2 uv = float2((id << 1) & 2, id & 2);
            output.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            output.Uv = uv;
            return output;
        }

        SamplerState LinearClamp : register(s0);

        // ---------------------------------------------------------------------------------------
        // Develop: NV12 camera frame -> RGB "program" image with framing, color and LUT applied.
        Texture2D<float> LumaTex : register(t0);
        Texture2D<float2> ChromaTex : register(t1);
        Texture3D<float4> LutTex : register(t2);

        cbuffer DevelopConstants : register(b0)
        {
            float4 UvRow0;     // source uv = (dot(UvRow0.xyz, (uv,1)), dot(UvRow1.xyz, (uv,1)))
            float4 UvRow1;
            float4 YuvRange;   // x: luma offset, y: luma scale, z: chroma scale, w: 0 = BT.709, 1 = BT.601
            float4 Adjust0;    // x: exposure gain, y: contrast, z: saturation, w: gamma
            float4 Adjust1;    // x: brightness, y: LUT amount, z: LUT size, w: vibrance
            float4 WhiteBalance; // rgb gains
        };

        float3 YuvToRgb(float y, float2 c)
        {
            y = (y - YuvRange.x) * YuvRange.y;
            c = (c - 0.50196) * YuvRange.z;
            if (YuvRange.w < 0.5)
                return float3(y + 1.5748 * c.y, y - 0.1873 * c.x - 0.4681 * c.y, y + 1.8556 * c.x);
            return float3(y + 1.402 * c.y, y - 0.344136 * c.x - 0.714136 * c.y, y + 1.772 * c.x);
        }

        float4 PSDevelop(VSOut input) : SV_Target
        {
            float3 p = float3(input.Uv, 1);
            float2 src = float2(dot(UvRow0.xyz, p), dot(UvRow1.xyz, p));
            if (any(src < 0) || any(src > 1))
                return float4(0, 0, 0, 1);

            float3 rgb = saturate(YuvToRgb(LumaTex.Sample(LinearClamp, src), ChromaTex.Sample(LinearClamp, src)));

            rgb *= Adjust0.x * WhiteBalance.rgb;
            rgb = (rgb - 0.5) * Adjust0.y + 0.5 + Adjust1.x;
            float luma = dot(rgb, float3(0.2126, 0.7152, 0.0722));
            float sat = Adjust0.z;
            if (Adjust1.w != 0)
            {
                // Vibrance: boost muted colors more than saturated ones.
                float chroma = max(rgb.r, max(rgb.g, rgb.b)) - min(rgb.r, min(rgb.g, rgb.b));
                sat *= 1 + Adjust1.w * (1 - saturate(chroma * 2));
            }
            rgb = saturate(lerp(luma.xxx, rgb, sat));
            rgb = pow(rgb, 1.0 / Adjust0.w);

            if (Adjust1.y > 0)
            {
                float n = Adjust1.z;
                float3 coord = rgb * ((n - 1) / n) + 0.5 / n;
                rgb = lerp(rgb, LutTex.SampleLevel(LinearClamp, coord, 0).rgb, Adjust1.y);
            }
            return float4(rgb, 1);
        }

        // Develop from an RGB(A) slate image (shown while no phone is connected).
        Texture2D<float4> SlateTex : register(t0);
        float4 PSSlate(VSOut input) : SV_Target
        {
            float3 p = float3(input.Uv, 1);
            float2 src = float2(dot(UvRow0.xyz, p), dot(UvRow1.xyz, p));
            if (any(src < 0) || any(src > 1))
                return float4(0, 0, 0, 1);
            return float4(SlateTex.Sample(LinearClamp, src).rgb, 1);
        }

        // ---------------------------------------------------------------------------------------
        // Outputs: program image -> virtual camera (NV12 planes or BGRA), aspect fit/fill.
        Texture2D<float4> ProgramTex : register(t0);

        cbuffer OutputConstants : register(b0)
        {
            float4 Map; // src = uv * Map.xy + Map.zw
        };

        float3 SampleProgram(float2 uv)
        {
            float2 src = uv * Map.xy + Map.zw;
            if (any(src < 0) || any(src > 1))
                return float3(0, 0, 0);
            return ProgramTex.Sample(LinearClamp, src).rgb;
        }

        // BT.709 limited range.
        float PSLuma(VSOut input) : SV_Target
        {
            return dot(SampleProgram(input.Uv), float3(0.1826, 0.6142, 0.0620)) + 0.0627451;
        }

        float2 PSChroma(VSOut input) : SV_Target
        {
            float3 rgb = SampleProgram(input.Uv);
            return float2(dot(rgb, float3(-0.1006, -0.3386, 0.4392)), dot(rgb, float3(0.4392, -0.3990, -0.0402))) + 0.50196;
        }

        float4 PSRgba(VSOut input) : SV_Target
        {
            return float4(SampleProgram(input.Uv), 1);
        }

        // ---------------------------------------------------------------------------------------
        // Preview with monitoring overlays (never sent to the virtual camera).
        cbuffer PreviewConstants : register(b0)
        {
            float4 PMap;      // src = uv * PMap.xy + PMap.zw (letterboxed)
            float4 PFlags;    // x: thirds grid, y: zebras, z: focus peaking, w: safe areas
            float4 PParams;   // x: zebra threshold, y: peaking threshold, z: time (s), w: vertical guide (1 = 9:16 box)
            float4 PSize;     // xy: program size (px), zw: 1 / program size
        };

        float LumaAt(float2 src)
        {
            return dot(ProgramTex.SampleLevel(LinearClamp, src, 0).rgb, float3(0.2126, 0.7152, 0.0722));
        }

        float LineMask(float2 coord, float2 lines)
        {
            // coord in program-normalized space; lines = number of divisions; 1 screen pixel wide.
            float2 f = abs(frac(coord * lines + 0.5) - 0.5) / max(fwidth(coord * lines), 1e-5);
            return saturate(1 - min(f.x, f.y));
        }

        float4 PSPreview(VSOut input) : SV_Target
        {
            float2 src = input.Uv * PMap.xy + PMap.zw;
            if (any(src < 0) || any(src > 1))
                return float4(0.075, 0.075, 0.085, 1);

            float3 rgb = ProgramTex.Sample(LinearClamp, src).rgb; // mip-filtered when the preview is smaller
            float luma = dot(rgb, float3(0.2126, 0.7152, 0.0722));

            if (PFlags.z > 0)
            {
                // Focus peaking: Sobel on luma in program pixels.
                float2 t = PSize.zw;
                float tl = LumaAt(src + t * float2(-1, -1)), tc = LumaAt(src + t * float2(0, -1)), tr = LumaAt(src + t * float2(1, -1));
                float ml = LumaAt(src + t * float2(-1, 0)), mr = LumaAt(src + t * float2(1, 0));
                float bl = LumaAt(src + t * float2(-1, 1)), bc = LumaAt(src + t * float2(0, 1)), br = LumaAt(src + t * float2(1, 1));
                float gx = (tr + 2 * mr + br) - (tl + 2 * ml + bl);
                float gy = (bl + 2 * bc + br) - (tl + 2 * tc + tr);
                float edge = sqrt(gx * gx + gy * gy);
                if (edge > PParams.y)
                    rgb = lerp(rgb, float3(1.0, 0.15, 0.25), 0.9);
            }

            if (PFlags.y > 0 && luma > PParams.x)
            {
                // Zebras: animated diagonal stripes over clipped highlights.
                float stripe = frac((input.Position.x + input.Position.y + PParams.z * 40) / 14);
                if (stripe < 0.5)
                    rgb = lerp(rgb, float3(0, 0, 0), 0.65);
            }

            if (PFlags.x > 0)
                rgb = lerp(rgb, float3(1, 1, 1), 0.45 * LineMask(src, float2(3, 3)));

            if (PFlags.w > 0)
            {
                // Action-safe (90%) and title-safe (80%) frames.
                float2 d = abs(src - 0.5);
                float2 px = fwidth(src);
                float action = step(abs(max(d.x / 0.45, d.y / 0.45) - 1) * 0.45, max(px.x, px.y));
                float title = step(abs(max(d.x / 0.40, d.y / 0.40) - 1) * 0.40, max(px.x, px.y));
                rgb = lerp(rgb, float3(1, 0.85, 0.2), 0.6 * saturate(action + title));
            }

            if (PParams.w > 0 && PSize.x > PSize.y)
            {
                // 9:16 Shorts/Reels guide: dim what a vertical crop would cut.
                float boxWidth = (PSize.y * 9.0 / 16.0) / PSize.x;
                if (abs(src.x - 0.5) > boxWidth * 0.5)
                    rgb *= 0.35;
            }
            return float4(rgb, 1);
        }
        """;
}
