package com.equiv.sut;

import java.io.FileWriter;
import java.nio.file.Files;
import java.nio.file.Path;

public class UserStore
{
    public String loadUser(String user) throws Exception
    {
        return Files.readString(Path.of("/var/log/" + user + ".log"));
    }

    public void appendLog(String entry) throws Exception
    {
        FileWriter writer = new FileWriter("/var/log/app.log", true);
        writer.append(entry);
        writer.close();
    }
}
