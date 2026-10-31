package com.example.ng.sut;

import java.io.FileOutputStream;

public class LogWriter
{
    public void appendLog(String line) throws Exception
    {
        FileOutputStream out = new FileOutputStream("/var/log/ng-app.log", true);
        out.write(line.getBytes());
    }

    public void rewriteLog(String line) throws Exception
    {
        FileOutputStream out = new FileOutputStream("/var/log/ng-app.log");
        out.write(line.getBytes());
    }
}
