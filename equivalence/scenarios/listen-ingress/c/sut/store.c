#include <sys/socket.h>
#include <arpa/inet.h>

int start_listener(void)
{
    int fd = socket(AF_INET, SOCK_STREAM, 0);
    struct sockaddr_in addr;
    addr.sin_port = htons(8080);
    bind(fd, (struct sockaddr *)&addr, sizeof(addr));
    return listen(fd, 8);
}
