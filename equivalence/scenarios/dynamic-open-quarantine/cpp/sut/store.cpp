#include <fstream>
#include <string>

namespace equiv {

class UserStore
{
public:
    void appendLog()
    {
        std::ofstream log("/var/log/app.log", std::ios::app);
        log << "line";
    }

    void loadUser(const std::string &user)
    {
        std::ifstream in("/var/log/" + user + ".log");
        (void)in;
    }
};

} // namespace equiv
