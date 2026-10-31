package com.example.core.sut;

import java.net.ServerSocket;
import java.net.URL;

public class StatusClient
{
    public URL statusUrl() throws Exception
    {
        return new URL("https://api.example.com/status");
    }

    public ServerSocket openListener() throws Exception
    {
        return new ServerSocket(8080);
    }
}
