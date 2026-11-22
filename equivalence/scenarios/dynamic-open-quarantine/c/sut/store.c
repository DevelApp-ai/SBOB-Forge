#include <stdio.h>

FILE *append_log(void)
{
    return fopen("/var/log/app.log", "a");
}

FILE *append_dynamic_log(const char *user)
{
    char path[256];
    snprintf(path, sizeof(path), "/var/log/%s.log", user);
    return fopen(path, "a");
}
