#include <fstream>

namespace equiv {

class LogStore
{
public:
    void appendLog()
    {
        std::ofstream log("/var/log/app.log", std::ios::app);
        log << "line";
    }
};

} // namespace equiv
