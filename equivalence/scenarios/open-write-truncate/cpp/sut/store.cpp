#include <fstream>

namespace equiv {

class CacheStore
{
public:
    void saveCache()
    {
        std::ofstream out("/var/lib/app/cache.json", std::ios::out | std::ios::trunc);
        out << "cache";
    }
};

} // namespace equiv
