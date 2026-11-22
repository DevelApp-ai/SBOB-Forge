namespace Equivalence.Sut;

public sealed class ToolRunner
{
    public int RunStatus() => System.Diagnostics.Process.Start("git", "status").ExitCode;
}
