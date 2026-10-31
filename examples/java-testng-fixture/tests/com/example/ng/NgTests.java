package com.example.ng.tests;

import com.example.ng.sut.LogWriter;
import org.testng.annotations.Test;

public class NgTests
{
    @Test
    public void appendsLog() throws Exception
    {
        new LogWriter().appendLog("entry");
    }

    @Test(enabled = false)
    public void legacyRewriteStillUsed() throws Exception
    {
        new LogWriter().rewriteLog("entry");
    }
}
