package com.equiv.sut;

public class ToolRunner
{
    public void runGitStatus() throws Exception
    {
        new ProcessBuilder("git", "status").start();
    }
}
