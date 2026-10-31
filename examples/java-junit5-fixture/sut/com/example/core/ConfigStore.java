package com.example.core.sut;

import java.nio.file.Files;
import java.nio.file.Path;

public class ConfigStore
{
    public String loadConfig() throws Exception
    {
        return Files.readString(Path.of("/etc/app/config.json"));
    }

    public void saveCache(String json) throws Exception
    {
        Files.write(Path.of("%PackageRoot%/cache/last.json"), json.getBytes());
    }

    public String loadUser(String user) throws Exception
    {
        return Files.readString(Path.of("/etc/app/users/" + user + ".json"));
    }

    public void resetCache() throws Exception
    {
        Files.delete(Path.of("%PackageRoot%/cache/last.json"));
    }
}
