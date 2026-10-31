using System.IO;

namespace Example.Core.Sut;

public sealed class ConfigStore
{
    public string LoadConfig() => File.ReadAllText("/etc/app/config.json");

    public void SaveCache(string json) => File.WriteAllText("%PackageRoot%/cache/last.json", json);

    public string LoadUser(string user) => File.ReadAllText($"/etc/app/users/{user}.json");

    public void Reset() => File.Delete("%PackageRoot%/cache/last.json");
}
