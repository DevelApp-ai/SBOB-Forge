using System.IO;

namespace Equivalence.Sut;

public sealed class ConfigStore
{
    public string LoadConfig() => File.ReadAllText("/etc/app/config.json");
}
