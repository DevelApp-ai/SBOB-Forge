package com.equiv.sut;

import java.io.FileWriter;

public class LogWriter
{
    public void appendLog(String entry) throws Exception
    {
        FileWriter writer = new FileWriter("/var/log/app.log", true);
        writer.append(entry);
        writer.close();
    }
}
