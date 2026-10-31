package com.example.ng.sut;

import java.net.Socket;

public class StatusClient
{
    public Socket openStatusSocket() throws Exception
    {
        return new Socket("api.example.com", 443);
    }
}
