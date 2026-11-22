#include <fstream>

namespace equiv {

class ConfigStore
{
public:
    void loadConfig()
    {
        std::ifstream in("/etc/app/config.json");
        char first;
        in.get(first);
    }
};

} // namespace equiv
