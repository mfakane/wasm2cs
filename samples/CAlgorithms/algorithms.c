// Freestanding C: no libc, allocator, imports, or undefined signed overflow.
static unsigned char buffer[256];

unsigned sum_bytes(const unsigned char *bytes, unsigned length)
{
    unsigned result = 0;
    for (unsigned i = 0; i < length; ++i) result += bytes[i];
    return result;
}

unsigned crc32(const unsigned char *bytes, unsigned length)
{
    unsigned crc = ~0u;
    for (unsigned i = 0; i < length; ++i) {
        crc ^= bytes[i];
        for (unsigned bit = 0; bit < 8; ++bit)
            crc = (crc >> 1) ^ ((0u - (crc & 1u)) & 0xedb88320u);
    }
    return ~crc;
}

unsigned char *buffer_ptr(void) { return buffer; }

unsigned f(unsigned seed, unsigned length)
{
    if (length > sizeof(buffer)) length = sizeof(buffer);
    for (unsigned i = 0; i < length; ++i) buffer[i] = (unsigned char)(seed + i * 13u);
    return crc32(buffer, length) ^ sum_bytes(buffer, length);
}
