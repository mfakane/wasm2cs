// Loops that Clang autovectorizes with -O3 -msimd128. Exports return i32 digests.
typedef unsigned char u8;
typedef unsigned int u32;

static float xs[1024];
static float ys[1024];
static u8 bytes[4096];

u8 *bytes_ptr(void) { return bytes; }

int init(int seed)
{
    u32 x = (u32)seed | 1u;
    for (int i = 0; i < 1024; i++)
    {
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        xs[i] = (float)(x & 0xffff) / 256.0f - 128.0f;
        ys[i] = (float)((x >> 16) & 0xff) / 16.0f;
    }
    for (int i = 0; i < 4096; i++) bytes[i] = (u8)(i * 7 + seed);
    return (int)x;
}

// y = a*x + y
int saxpy(int n, int a_milli)
{
    if (n < 0 || n > 1024) return -1;
    float a = (float)a_milli / 1000.0f;
    for (int i = 0; i < n; i++) ys[i] = a * xs[i] + ys[i];
    union { float f; int i; } bits = { ys[n > 0 ? n - 1 : 0] };
    return bits.i;
}

int dot(int n)
{
    if (n < 0 || n > 1024) return -1;
    float total = 0;
    for (int i = 0; i < n; i++) total += xs[i] * ys[i];
    return (int)total;
}

int byte_sum(int n)
{
    if (n < 0 || n > 4096) return -1;
    u32 total = 0;
    for (int i = 0; i < n; i++) total += bytes[i];
    return (int)total;
}

int byte_max(int n)
{
    if (n <= 0 || n > 4096) return -1;
    u8 best = 0;
    for (int i = 0; i < n; i++) best = bytes[i] > best ? bytes[i] : best;
    return best;
}

int clamp_scale(int n)
{
    if (n < 0 || n > 1024) return -1;
    u32 digest = 0;
    for (int i = 0; i < n; i++)
    {
        float v = xs[i] * 2.0f;
        v = v < -100.0f ? -100.0f : (v > 100.0f ? 100.0f : v);
        xs[i] = v;
    }
    for (int i = 0; i < n; i++) digest = digest * 33u + (u32)(int)xs[i];
    return (int)digest;
}
