package com.equiv.tests;

import com.equiv.sut.StatusClient;
import org.junit.jupiter.api.Test;

public class StatusClientTests
{
    @Test
    public void fetchesStatus() throws Exception
    {
        new StatusClient().statusUrl();
    }
}
