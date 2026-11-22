package com.equiv.tests;

import com.equiv.sut.ToolRunner;
import org.junit.jupiter.api.Test;

public class ToolRunnerTests
{
    @Test
    public void runsGitStatus() throws Exception
    {
        new ToolRunner().runGitStatus();
    }
}
