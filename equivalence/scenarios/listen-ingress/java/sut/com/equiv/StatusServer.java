package com.equiv.sut;

import java.net.ServerSocket;

public class StatusServer
{
    public ServerSocket openListener() throws Exception
    {
        return new ServerSocket(8080);
    }
}
