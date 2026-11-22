using System.IO;

namespace Equivalence.Sut;

public sealed class ConfigStore
{
    public void SaveCache(string json) => File.WriteAllText("/var/lib/app/cache.json", json);
}
