package com.equiv.sut;

import java.net.URL;

public class StatusClient
{
    public URL statusUrl() throws Exception
    {
        return new URL("https://api.example.com/status");
    }
}
