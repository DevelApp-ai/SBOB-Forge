using System.IO;

namespace Equivalence.Sut;

public sealed class UserStore
{
    public string LoadUser(string user) => File.ReadAllText($"/var/log/{user}.log");

    public void AppendLog(string entry) => File.AppendAllText("/var/log/app.log", entry);
}
