package com.example.ng.sut;

public class ToolRunner
{
    public void runGitStatus() throws Exception
    {
        Runtime.getRuntime().exec("git status");
    }
}
