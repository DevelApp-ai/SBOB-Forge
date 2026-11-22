using System.IO;

namespace Equivalence.Sut;

public sealed class ConfigStore
{
    public void AppendLog(string entry) => File.AppendAllText("/var/log/app.log", entry);
}
