using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

// 生成体积云使用的 128^3 可平铺 3D 噪声（参考 Horizon Zero Dawn / Nubis 的通道布局）：
// R = Perlin-Worley（成团的主体形状），G/B/A = 三个频段的反相 Worley FBM（菜花状的翻滚细节）。
// 所有噪声的晶格都按周期取模，保证纹理在 Repeat 模式下无缝平铺。
public static class CreatePLCloudNoise
{
    private const int Size = 128;
    private const string AssetPath = "Assets/PLCloud/Resources/CloudShapeNoise128.asset";

    [MenuItem("Tools/PLCloud/Generate Cloud Noise 128")]
    public static void Generate()
    {
        int voxelCount = Size * Size * Size;
        float[][] channels = { new float[voxelCount], new float[voxelCount], new float[voxelCount], new float[voxelCount] };

        // 每个 z 切片独立计算，只用纯数学函数，可以安全地并行。
        Parallel.For(0, Size, z =>
        {
            for (int y = 0; y < Size; ++y)
            {
                for (int x = 0; x < Size; ++x)
                {
                    Vector3 p = new Vector3((x + 0.5f) / Size, (y + 0.5f) / Size, (z + 0.5f) / Size);
                    float perlin = PerlinFbm(p, 4, 3, 7u) * 0.5f + 0.5f;
                    float w4 = 1.0f - Worley(p, 4, 11u);
                    float w8 = 1.0f - Worley(p, 8, 23u);
                    float w16 = 1.0f - Worley(p, 16, 37u);
                    float w32 = 1.0f - Worley(p, 32, 51u);
                    float worleyFbm = w4 * 0.625f + w8 * 0.25f + w16 * 0.125f;

                    int index = x + y * Size + z * Size * Size;
                    // Perlin 提供连续的大团块，Worley 把团块边缘"膨胀"成圆鼓鼓的云包。
                    channels[0][index] = Remap(Mathf.Clamp01(perlin), 0.0f, 1.0f, worleyFbm, 1.0f);
                    channels[1][index] = worleyFbm;
                    channels[2][index] = w8 * 0.625f + w16 * 0.25f + w32 * 0.125f;
                    channels[3][index] = w16 * 0.75f + w32 * 0.25f;
                }
            }
        });

        // 每个通道拉伸到完整的 [0, 1]，让 shader 里的覆盖率和侵蚀阈值有稳定的含义。
        foreach (float[] channel in channels)
        {
            Normalize(channel);
        }

        Color32[] pixels = new Color32[voxelCount];
        for (int i = 0; i < voxelCount; ++i)
        {
            pixels[i] = new Color32(ToByte(channels[0][i]), ToByte(channels[1][i]), ToByte(channels[2][i]), ToByte(channels[3][i]));
        }

        // 带 mip：远处的云按距离采样低 mip，省带宽也减少闪烁
        Texture3D texture = new Texture3D(Size, Size, Size, TextureFormat.RGBA32, true);
        texture.name = "CloudShapeNoise128";
        texture.wrapMode = TextureWrapMode.Repeat;
        texture.filterMode = FilterMode.Trilinear;
        texture.SetPixels32(pixels, 0);
        texture.Apply(true);

        // 已存在时原地覆盖，保留 GUID，材质引用不会丢失。
        Texture3D existing = AssetDatabase.LoadAssetAtPath<Texture3D>(AssetPath);
        if (existing == null)
        {
            AssetDatabase.CreateAsset(texture, AssetPath);
        }
        else
        {
            EditorUtility.CopySerialized(texture, existing);
            Object.DestroyImmediate(texture);
            EditorUtility.SetDirty(existing);
        }

        AssetDatabase.SaveAssets();
        Debug.Log("Cloud noise generated at " + AssetPath);
    }

    private static float Remap(float value, float oldMin, float oldMax, float newMin, float newMax)
    {
        return newMin + (value - oldMin) / (oldMax - oldMin) * (newMax - newMin);
    }

    private static byte ToByte(float value)
    {
        return (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * 255.0f);
    }

    private static void Normalize(float[] values)
    {
        float min = float.MaxValue;
        float max = float.MinValue;
        foreach (float value in values)
        {
            min = Mathf.Min(min, value);
            max = Mathf.Max(max, value);
        }

        float range = Mathf.Max(max - min, 0.00001f);
        for (int i = 0; i < values.Length; ++i)
        {
            values[i] = (values[i] - min) / range;
        }
    }

    private static int Wrap(int value, int period)
    {
        int result = value % period;
        return result < 0 ? result + period : result;
    }

    private static uint Hash(int x, int y, int z, uint seed)
    {
        uint h = seed * 0x9E3779B9u;
        h ^= (uint)x * 0x8DA6B343u;
        h ^= (uint)y * 0xD8163841u;
        h ^= (uint)z * 0xCB1AB31Fu;
        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;
        return h;
    }

    private static float HashToFloat(uint h)
    {
        return (h & 0x00FFFFFFu) / 16777216.0f;
    }

    // 可平铺 Worley：返回到最近特征点的距离（单位：晶格），邻居晶格按周期取模后再哈希。
    private static float Worley(Vector3 p, int cells, uint seed)
    {
        float px = p.x * cells;
        float py = p.y * cells;
        float pz = p.z * cells;
        int cx = Mathf.FloorToInt(px);
        int cy = Mathf.FloorToInt(py);
        int cz = Mathf.FloorToInt(pz);
        float minDistanceSq = 10.0f;

        for (int dz = -1; dz <= 1; ++dz)
        {
            for (int dy = -1; dy <= 1; ++dy)
            {
                for (int dx = -1; dx <= 1; ++dx)
                {
                    int nx = cx + dx;
                    int ny = cy + dy;
                    int nz = cz + dz;
                    int wx = Wrap(nx, cells);
                    int wy = Wrap(ny, cells);
                    int wz = Wrap(nz, cells);
                    float fx = nx + HashToFloat(Hash(wx, wy, wz, seed)) - px;
                    float fy = ny + HashToFloat(Hash(wx, wy, wz, seed + 1u)) - py;
                    float fz = nz + HashToFloat(Hash(wx, wy, wz, seed + 2u)) - pz;
                    minDistanceSq = Mathf.Min(minDistanceSq, fx * fx + fy * fy + fz * fz);
                }
            }
        }

        return Mathf.Min(Mathf.Sqrt(minDistanceSq), 1.0f);
    }

    private static float Fade(float t)
    {
        return t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);
    }

    private static float Gradient(uint h, float x, float y, float z)
    {
        int hh = (int)(h & 15u);
        float u = hh < 8 ? x : y;
        float v = hh < 4 ? y : (hh == 12 || hh == 14 ? x : z);
        return ((hh & 1) == 0 ? u : -u) + ((hh & 2) == 0 ? v : -v);
    }

    // 可平铺 Perlin 梯度噪声，输出约在 [-1, 1]。
    private static float Perlin(Vector3 p, int period, uint seed)
    {
        float px = p.x * period;
        float py = p.y * period;
        float pz = p.z * period;
        int x0 = Mathf.FloorToInt(px);
        int y0 = Mathf.FloorToInt(py);
        int z0 = Mathf.FloorToInt(pz);
        float fx = px - x0;
        float fy = py - y0;
        float fz = pz - z0;
        float u = Fade(fx);
        float v = Fade(fy);
        float w = Fade(fz);

        float Corner(int dx, int dy, int dz)
        {
            uint h = Hash(Wrap(x0 + dx, period), Wrap(y0 + dy, period), Wrap(z0 + dz, period), seed);
            return Gradient(h, fx - dx, fy - dy, fz - dz);
        }

        float x00 = Mathf.Lerp(Corner(0, 0, 0), Corner(1, 0, 0), u);
        float x10 = Mathf.Lerp(Corner(0, 1, 0), Corner(1, 1, 0), u);
        float x01 = Mathf.Lerp(Corner(0, 0, 1), Corner(1, 0, 1), u);
        float x11 = Mathf.Lerp(Corner(0, 1, 1), Corner(1, 1, 1), u);
        return Mathf.Lerp(Mathf.Lerp(x00, x10, v), Mathf.Lerp(x01, x11, v), w);
    }

    private static float PerlinFbm(Vector3 p, int basePeriod, int octaves, uint seed)
    {
        float sum = 0.0f;
        float amplitude = 1.0f;
        float totalAmplitude = 0.0f;
        int period = basePeriod;
        for (int octave = 0; octave < octaves; ++octave)
        {
            sum += Perlin(p, period, seed + (uint)octave * 101u) * amplitude;
            totalAmplitude += amplitude;
            amplitude *= 0.5f;
            period *= 2;
        }

        return sum / totalAmplitude;
    }
}
