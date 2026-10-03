// Freestanding C workload for T02 re-measurement. Each export returns an i32
// digest so the Node.js differential harness can compare it.
typedef unsigned char u8;
typedef signed char i8;
typedef unsigned int u32;
typedef unsigned long long u64;
typedef long long i64;

static u8 buffer[4096];
static u8 scratch[4096];

u8 *buffer_ptr(void) { return buffer; }

// 64-bit FNV-1a over the buffer, folded to 32 bits.
int fnv64(int length)
{
    if (length < 0) length = 0;
    if (length > (int)sizeof(buffer)) length = sizeof(buffer);
    u64 hash = 0xcbf29ce484222325ull;
    for (int i = 0; i < length; i++)
    {
        hash ^= buffer[i];
        hash *= 0x100000001b3ull;
    }
    return (int)(hash ^ (hash >> 32));
}

// Copy, fill, and move through the compiler's memcpy/memset/memmove lowering.
int copy_fill(int offset, int length, int value)
{
    if (offset < 0 || length < 0 || offset > 2048 || length > 2048) return -1;
    __builtin_memcpy(scratch, buffer + offset, (unsigned)length);
    __builtin_memset(scratch + length, value, 64);
    __builtin_memmove(scratch + 1, scratch, (unsigned)length);
    u32 sum = 0;
    for (int i = 0; i < length + 65; i++) sum = sum * 31u + scratch[i];
    return (int)sum;
}

// Signed byte arithmetic (sign extension).
int signed_bytes(int length)
{
    if (length < 0 || length > (int)sizeof(buffer)) return 0;
    int total = 0;
    for (int i = 0; i < length; i++) total += (i8)buffer[i] * ((i & 1) ? -3 : 5);
    return total;
}

// Floating-point statistics over the first n bytes, then float -> int conversions.
int stats(int length, int scale)
{
    if (length <= 0 || length > (int)sizeof(buffer)) return 0;
    double sum = 0, squares = 0;
    for (int i = 0; i < length; i++)
    {
        double x = (double)buffer[i] / 7.0 - 12.5;
        sum += x;
        squares += x * x;
    }
    double mean = sum / length;
    double variance = squares / length - mean * mean;
    double deviation = __builtin_sqrt(variance < 0 ? 0 : variance);
    float scaled = (float)(deviation * scale);
    int a = (int)(mean * 1000.0);
    int b = (int)scaled;
    i64 c = (i64)(variance * 1e6);
    return a ^ (b << 8) ^ (int)c ^ (int)(c >> 32);
}

// A small stack bytecode interpreter: dense switch -> br_table.
enum { OP_PUSH, OP_ADD, OP_SUB, OP_MUL, OP_DUP, OP_SWAP, OP_NEG, OP_SHL, OP_XOR, OP_JNZ, OP_HALT };

int interpret(int program_length, int seed)
{
    int stack[64];
    int sp = 0, pc = 0, steps = 0;
    stack[sp++] = seed;
    if (program_length < 0 || program_length > 256) return -1;
    while (pc < program_length && steps++ < 10000)
    {
        u8 op = buffer[pc++] % 11;
        switch (op)
        {
        case OP_PUSH: if (sp < 64) stack[sp++] = buffer[pc++ % 256]; break;
        case OP_ADD: if (sp > 1) { sp--; stack[sp - 1] += stack[sp]; } break;
        case OP_SUB: if (sp > 1) { sp--; stack[sp - 1] -= stack[sp]; } break;
        case OP_MUL: if (sp > 1) { sp--; stack[sp - 1] *= stack[sp]; } break;
        case OP_DUP: if (sp > 0 && sp < 64) { stack[sp] = stack[sp - 1]; sp++; } break;
        case OP_SWAP: if (sp > 1) { int t = stack[sp - 1]; stack[sp - 1] = stack[sp - 2]; stack[sp - 2] = t; } break;
        case OP_NEG: if (sp > 0) stack[sp - 1] = -stack[sp - 1]; break;
        case OP_SHL: if (sp > 0) stack[sp - 1] = (int)((u32)stack[sp - 1] << 3); break;
        case OP_XOR: if (sp > 1) { sp--; stack[sp - 1] ^= stack[sp]; } break;
        case OP_JNZ: if (sp > 0 && stack[sp - 1] != 0 && pc > 4) pc -= 4; break;
        default: return sp > 0 ? stack[sp - 1] : 0;
        }
    }
    return sp > 0 ? stack[sp - 1] ^ steps : steps;
}

// Insertion sort with a comparator chosen at run time: call_indirect.
typedef int (*compare_fn)(int, int);
static int ascending(int a, int b) { return a - b; }
static int descending(int a, int b) { return b - a; }
static int by_low_nibble(int a, int b) { return (a & 15) - (b & 15); }
static compare_fn comparators[] = { ascending, descending, by_low_nibble };

int sort_bytes(int length, int which)
{
    if (length < 0 || length > 512 || which < 0 || which > 2) return -1;
    compare_fn compare = comparators[which];
    int values[512];
    for (int i = 0; i < length; i++) values[i] = buffer[i];
    for (int i = 1; i < length; i++)
    {
        int v = values[i], j = i - 1;
        while (j >= 0 && compare(values[j], v) > 0) { values[j + 1] = values[j]; j--; }
        values[j + 1] = v;
    }
    u32 digest = 0;
    for (int i = 0; i < length; i++) { buffer[i] = (u8)values[i]; digest = digest * 131u + (u32)values[i]; }
    return (int)digest;
}

// Fill the buffer deterministically from a seed (xorshift32).
int fill(int seed, int length)
{
    if (length < 0 || length > (int)sizeof(buffer)) return -1;
    u32 x = (u32)seed | 1u;
    for (int i = 0; i < length; i++)
    {
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        buffer[i] = (u8)x;
    }
    return (int)x;
}
