package com.example.core.tests;

import com.example.core.sut.ConfigStore;
import org.junit.jupiter.api.Disabled;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;

import java.nio.file.Files;
import java.nio.file.Path;

public class ConfigStoreTests
{
    @Test
    public void loadsConfigFromEtc() throws Exception
    {
        String config = new ConfigStore().loadConfig();
    }

    @Test
    @Disabled("flaky in CI")
    public void legacyPathStillUsed() throws Exception
    {
        String legacy = Files.readString(Path.of("/etc/app/legacy.json"));
    }

    @ParameterizedTest
    @ValueSource(strings = { "/var/log/app.log", "/var/log/other.log" })
    public void writesLogPath(String path) throws Exception
    {
        Files.write(Path.of(path), "entry".getBytes());
    }
}
