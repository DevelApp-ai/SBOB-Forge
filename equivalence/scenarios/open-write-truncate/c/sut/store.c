#include <fcntl.h>
#include <unistd.h>

int save_cache(const char *payload)
{
    int fd = open("/var/lib/app/cache.json", O_WRONLY | O_CREAT | O_TRUNC, 0644);
    if (fd < 0)
        return -1;
    close(fd);
    return 0;
}
