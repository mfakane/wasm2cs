#include <unistd.h>

int main(void)
{
    static const char message[] = "exit 3\n";
    write(STDERR_FILENO, message, sizeof(message) - 1);
    return 3;
}
