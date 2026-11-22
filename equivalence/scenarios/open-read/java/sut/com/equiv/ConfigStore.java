package com.equiv.sut;

import java.nio.file.Files;
import java.nio.file.Path;

public class ConfigStore
{
    public String loadConfig() throws Exception
    {
        return Files.readString(Path.of("/etc/app/config.json"));
    }
}
