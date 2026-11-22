package com.equiv.tests;

import com.equiv.sut.LogWriter;
import org.junit.jupiter.api.Test;

public class LogWriterTests
{
    @Test
    public void appendsLog() throws Exception
    {
        new LogWriter().appendLog("entry");
    }
}
