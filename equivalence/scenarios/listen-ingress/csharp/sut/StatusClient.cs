using System.Net;

namespace Equivalence.Sut;

public sealed class StatusServer
{
    public void StartListener()
    {
        var listener = new TcpListener(IPAddress.Any, 8080);
        listener.Start();
    }
}
