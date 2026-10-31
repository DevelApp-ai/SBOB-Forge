using System.Net;

namespace Example.Core.Sut;

public sealed class StatusClient
{
    public string FetchStatus()
    {
        using var http = new System.Net.Http.HttpClient();
        return http.GetStringAsync("https://api.example.com/status").Result;
    }

    public void StartListener()
    {
        var listener = new TcpListener(IPAddress.Any, 8080);
        listener.Start();
    }
}
