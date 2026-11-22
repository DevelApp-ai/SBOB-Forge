package com.equiv.sut;

import java.nio.file.Files;
import java.nio.file.Path;

public class CacheStore
{
    public void saveCache(String json) throws Exception
    {
        Files.write(Path.of("/var/lib/app/cache.json"), json.getBytes());
    }
}
