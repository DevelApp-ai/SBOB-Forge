#include <stdio.h>

FILE *append_log(void)
{
    return fopen("/var/log/app.log", "a");
}
