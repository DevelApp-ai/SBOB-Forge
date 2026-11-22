package com.equiv.tests;

import com.equiv.sut.ConfigStore;
import org.junit.jupiter.api.Test;

public class ConfigStoreTests
{
    @Test
    public void loadsConfigFromEtc() throws Exception
    {
        String config = new ConfigStore().loadConfig();
    }
}
