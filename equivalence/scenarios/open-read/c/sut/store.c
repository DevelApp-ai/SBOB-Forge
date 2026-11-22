#include <stdio.h>

FILE *open_config(void)
{
    return fopen("/etc/app/config.json", "r");
}
