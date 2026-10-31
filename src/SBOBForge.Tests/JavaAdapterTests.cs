using System.Linq;
using SBOBForge.Adapters.Java;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class JavaAdapterTests
{
    private static CodeModel ParseJunit(string source) =>
        new JavaSourceParser().Parse("src/test/java/com/example/core/MixedTests.java", source);

    [Fact]
    public void JUnit5ExtractsPlainDisabledAndParameterizedEntries()
    {
        var model = ParseJunit("""
            package com.example.core;

            import org.junit.jupiter.api.Test;
            import org.junit.jupiter.api.Disabled;
            import org.junit.jupiter.params.ParameterizedTest;
            import org.junit.jupiter.params.provider.ValueSource;

            public class MixedTests
            {
                @Test
                public void plain() { }

                @Test
                @Disabled
                public void off() { }

                @ParameterizedTest
                @ValueSource(strings = { "/a", "/b" })
                public void cases(String path) { }
            }
            """);
        var adapter = new JUnit5Adapter();
        Assert.True(adapter.CanAdapt(model));
        Assert.Equal("junit5", adapter.FrameworkId);

        var entries = adapter.ExtractEntries(model);
        Assert.Equal(4, entries.Count);

        var plain = Assert.Single(entries, e => e.TestId == "com.example.core.MixedTests.plain");
        Assert.True(plain.Enabled);
        Assert.Empty(plain.TheoryCaseValues);

        var off = Assert.Single(entries, e => e.TestId == "com.example.core.MixedTests.off");
        Assert.False(off.Enabled);

        Assert.Single(entries, e => e.TestId == "com.example.core.MixedTests.cases[0]"
            && e.TheoryCaseValues.SequenceEqual(new[] { "/a" }) && e.Enabled);
        Assert.Single(entries, e => e.TestId == "com.example.core.MixedTests.cases[1]"
            && e.TheoryCaseValues.SequenceEqual(new[] { "/b" }) && e.Enabled);
    }

    [Fact]
    public void TestNGExtractsEnabledAndDisabledEntries()
    {
        var model = new JavaSourceParser().Parse("src/test/java/NgTests.java", """
            package com.example.testng;

            import org.testng.annotations.Test;

            public class NgTests
            {
                @Test
                public void on() { }

                @Test(enabled = false)
                public void skipped() { }
            }
            """);
        var adapter = new TestNGAdapter();
        Assert.True(adapter.CanAdapt(model));
        Assert.Equal("testng", adapter.FrameworkId);

        var entries = adapter.ExtractEntries(model);
        Assert.Equal(2, entries.Count);
        var on = Assert.Single(entries, e => e.TestId == "com.example.testng.NgTests.on");
        Assert.True(on.Enabled);
        var skipped = Assert.Single(entries, e => e.TestId == "com.example.testng.NgTests.skipped");
        Assert.False(skipped.Enabled);
    }

    [Fact]
    public void RegistryExtractsOnlyAdaptingFramework()
    {
        var registry = new JavaTestFrameworkRegistry();
        var junitModel = ParseJunit("""
            package com.example.core;
            import org.junit.jupiter.api.Test;
            public class T
            {
                @Test
                public void only() { }
            }
            """);
        var junitEntries = registry.ExtractEntries(junitModel);
        var entry = Assert.Single(junitEntries);
        Assert.Equal("com.example.core.T.only", entry.TestId);

        var plainModel = new JavaSourceParser().Parse("Plain.java", "public class Plain { }");
        Assert.Empty(registry.ExtractEntries(plainModel));
    }
}
