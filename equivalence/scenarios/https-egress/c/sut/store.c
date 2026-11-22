#include <netdb.h>

int fetch_status(void)
{
    struct addrinfo hints, *res;
    getaddrinfo("api.example.com", "443", &hints, &res);
    return 0;
}
