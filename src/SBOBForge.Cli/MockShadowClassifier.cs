using System;
using SourceLang = SBOBForge.Cli.GeneratePipeline.SourceLang;

namespace SBOBForge.Cli
{
    /// <summary>
    /// M7 coverage report v1 (TDS 7.4): classifies invocation rows that belong to a
    /// known mock framework, so generate can report them as mock-shadowed warnings
    /// rather than absorbing them as generic harness noise. A mock inside an enabled
    /// test entry means the SUT dependency it intercepts is not really exercised:
    /// behavior declared in the sbob is under-verified for that case and is a leading
    /// downstream false-positive source.
    /// Deliberately lives in the CLI assembly (not the catalogs) so adding patterns
    /// cannot churn catalog sha256 locks pinned in frozen fixtures.
    /// </summary>
    internal static class MockShadowClassifier
    {
        /// <summary>
        /// Returns the mock-framework kind when the invocation signature matches a
        /// known mock API for the source language, otherwise null.
        /// </summary>
        internal static string? Kind(SourceLang lang, string? receiver, string? method)
        {
            if (method is null || method.Length == 0)
                return null;

            return lang switch
            {
                SourceLang.CSharp => CSharpKind(receiver, method),
                SourceLang.Java => JavaKind(receiver, method),
                SourceLang.Python => PythonKind(receiver, method),
                SourceLang.Go => GoKind(receiver, method),
                SourceLang.TypeScript => TypeScriptKind(receiver, method),
                SourceLang.Php => PhpKind(receiver, method),
                SourceLang.PowerShell => PowerShellKind(receiver, method),
                _ => null
            };
        }

        // NSubstitute: Substitute.For[..]() on the SUT dependency, and .Received[..]()
        // assertions; Moq: mocks whose receiver name contains "mock" plus the
        // Setup/Verify/Returns stubbing family. Receiver-invariant matching keeps
        // this list small.
        private static string? CSharpKind(string? receiver, string method) =>
            receiver is "Substitute" && method.StartsWith("For", StringComparison.Ordinal) ? "nsubstitute"
            : method is "Received" or "ReceivedWithAnyArgs" ? "nsubstitute"
            : IsMoqStub(receiver, method) ? "moq"
            : null;

        private static string? JavaKind(string? receiver, string method) =>
            receiver is null || receiver.Length == 0 || receiver.Equals("Mockito", StringComparison.Ordinal)
                ? MockitoMethod(method) ? "mockito" : null
                : null;

        private static string? PythonKind(string? receiver, string method) =>
            method is "patch" or "MagicMock" ? "unittest.mock"
            : receiver is "monkeypatch" && method == "setattr" ? "pytest.monkeypatch"
            : null;

        private static string? GoKind(string? receiver, string method) =>
            receiver is "gomock" && method == "NewController" ? "gomock"
            : receiver is "mock" && method.StartsWith("NewMock", StringComparison.Ordinal) ? "gomock"
            : null;

        private static string? TypeScriptKind(string? receiver, string method) =>
            receiver is "jest" or "vi" && JestMethod(method) ? JestOf(receiver)
            : null;

        private static string? PhpKind(string? receiver, string method) =>
            receiver is null || receiver.Equals("this", StringComparison.Ordinal)
                ? (method is "createMock" or "getMockBuilder" ? "phpunit"
                   : method is "prophesize" or "reveal" ? "prophecy"
                   : null)
            : null;

        private static string? PowerShellKind(string? receiver, string method) =>
            receiver is null && method.Equals("Mock", StringComparison.Ordinal) ? "pester" : null;

        private static readonly string[] MoqStubs =
        {
            "Setup", "SetupGet", "SetupSet", "SetupSequence",
            "Verify", "VerifyAll", "VerifyNoOtherCalls",
            "Returns", "ReturnsAsync", "Throws", "ThrowsAsync"
        };

        private static bool IsMoqStub(string? receiver, string method) =>
            receiver is not null
            && receiver.IndexOf("mock", StringComparison.OrdinalIgnoreCase) >= 0
            && Array.IndexOf(MoqStubs, method) >= 0;

        private static bool MockitoMethod(string m) =>
            m is "mock" or "spy" or "when" or "verify" or "verifyNoMoreInteractions"
                or "doReturn" or "doThrow" or "doAnswer";

        private static bool JestMethod(string m) =>
            m is "fn" or "mock" or "spyOn" or "mockReturnValue" or "mockResolvedValue" or "mockImplementation";

        private static string JestOf(string receiver) => receiver == "jest" ? "jest" : "vitest";
    }
}
