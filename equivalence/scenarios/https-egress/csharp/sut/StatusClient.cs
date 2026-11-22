namespace Equivalence.Sut;

public sealed class StatusClient
{
    public string FetchStatus()
    {
        using var http = new System.Net.Http.HttpClient();
        return http.GetStringAsync("https://api.example.com/status").Result;
    }
}
