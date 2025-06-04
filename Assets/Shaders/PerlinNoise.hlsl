#ifndef PERLIN_NOISE_INCLUDED
#define PERLIN_NOISE_INCLUDED

// Optimised hash function: 3 ALU
half hash_optimised(half2 p)
{
    return frac(sin(dot(p, half2(127.1h, 311.7h))) * 43758.5453h);
}

// Optimised 2D noise: ~25 ALU
half noise2d_optimised(half2 p)
{
    half2 i = floor(p);
    half2 f = frac(p);

    half a = hash_optimised(i);
    half b = hash_optimised(i + half2(1,0));
    half c = hash_optimised(i + half2(0,1));
    half d = hash_optimised(i + half2(1,1));

    half2 u = f * f * (3.0h - 2.0h * f); // smoothstep
    return lerp(lerp(a,b,u.x), lerp(c,d,u.x), u.y);
}

// Optimised terrain with 2 octaves: ~60 ALU total
half terrain_optimised(half2 pos, half frequency)
{
    half2 p = pos * frequency;
    half height = 0.0h;
    half amp = 0.5h;
    half freq = 1.0h;

    // Octave 1
    height += noise2d_optimised(p * freq) * amp;
    amp *= 0.5h; freq *= 2.0h;

    // Octave 2
    height += noise2d_optimised(p * freq) * amp;

    return height;
}

// Fast normal calculation using finite differences: ~190 ALU
half3 getNormal_optimised(half2 worldPos, half frequency, int quality)
{
    half eps = 0.1h / frequency; // Scale epsilon with frequency

    half h_center = terrain_optimised(worldPos, frequency);
    half h_right = terrain_optimised(worldPos + half2(eps, 0), frequency);
    half h_up = terrain_optimised(worldPos + half2(0, eps), frequency);

    half dhdx = (h_right - h_center) / eps;
    half dhdy = (h_up - h_center) / eps;

    return normalize(half3(-dhdx, 1.0h, -dhdy));
}

// Optimised version - 60 ALU
half getHeight_optimised(half2 worldPos, half frequency)
{
    return terrain_optimised(worldPos, frequency);
}

// With normals - varies by quality
half4 getHeightAndNormal(half2 worldPos, half frequency, int quality)
{
    return half4(getHeight_optimised(worldPos, frequency), getNormal_optimised(worldPos, frequency, 0));
}

#endif // PERLIN_NOISE_INCLUDED
