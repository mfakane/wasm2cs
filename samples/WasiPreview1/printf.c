#include <stdio.h>

int main(void)
{
    int values[] = { 3, -17, 42, 1000 };
    int total = 0;
    for (int i = 0; i < 4; i++) total += values[i];
    printf("count=%d total=%d mean=%.3f hex=%#x name=%s\n", 4, total, total / 4.0, total, "wasi");
    fprintf(stderr, "warn: %5.1f%%\n", 12.345);
    return 0;
}
