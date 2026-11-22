package com.equiv.tests;

import com.equiv.sut.StatusServer;
import org.junit.jupiter.api.Test;

public class StatusServerTests
{
    @Test
    public void opensListener() throws Exception
    {
        new StatusServer().openListener();
    }
}
