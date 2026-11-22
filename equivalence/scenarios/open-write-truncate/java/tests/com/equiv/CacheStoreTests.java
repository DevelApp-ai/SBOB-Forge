package com.equiv.tests;

import com.equiv.sut.CacheStore;
import org.junit.jupiter.api.Test;

public class CacheStoreTests
{
    @Test
    public void savesCache() throws Exception
    {
        new CacheStore().saveCache("{}");
    }
}
