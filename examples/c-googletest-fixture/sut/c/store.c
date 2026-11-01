#include <stdio.h>
#include <fcntl.h>
#include <unistd.h>
#include <netdb.h>

FILE *open_config(void)
{
    return fopen("/etc/app/config.json", "r");
}

int save_cache(const char *payload)
{
    int fd = open("%PackageRoot%/cache/last.json", O_WRONLY | O_CREAT | O_TRUNC, 0644);
    if (fd < 0)
        return -1;
    close(fd);
    return 0;
}

FILE *append_log(void)
{
    return fopen("/var/log/c-app.log", "a");
}

int run_git_status(void)
{
    execvp("git", (char *[]){"/usr/bin/git", "status", NULL});
    return 0;
}

int fetch_status(void)
{
    struct addrinfo hints, *res;
    getaddrinfo("api.example.com", "443", &hints, &res);
    return 0;
}

FILE *load_user(const char *user)
{
    char path[256];
    snprintf(path, sizeof(path), "/etc/app/users/%s.json", user);
    return fopen(path, "r");
}

void clean_cache(void)
{
    unlink("%PackageRoot%/cache/last.json");
}
