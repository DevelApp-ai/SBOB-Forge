#include <boost/asio.hpp>

namespace equiv {

class StatusClient
{
public:
    void syncRemote()
    {
        boost::asio::ip::tcp::resolver resolver;
        resolver.resolve("api.example.com", "443");
    }
};

} // namespace equiv
