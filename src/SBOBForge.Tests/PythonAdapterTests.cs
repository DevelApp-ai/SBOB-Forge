using System.Linq;
using SBOBForge.Adapters.Python;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class PythonAdapterTests
{
    private static CodeModel Parse(string source) =>
        new PythonSourceParser().Parse("tests/test_core.py", source);

    [Fact]
    public void PytestExtractsModuleFunctionsSkipsAndParameterizedCases()
    {
        var model = Parse("""
            import pytest

            def test_plain():
                pass

            @pytest.mark.skip
            def test_skipped():
                pass

            @pytest.mark.parametrize("name", ["a", "b"])
            def test_cases(name):
                pass
            """);
        var adapter = new PytestAdapter();
        Assert.True(adapter.CanAdapt(model));
        Assert.Equal("pytest", adapter.FrameworkId);

        var entries = adapter.ExtractEntries(model);
        Assert.Equal(4, entries.Count);
        Assert.Single(entries, e => e.TestId == "tests/test_core.py::test_plain" && e.Enabled);
        Assert.Single(entries, e => e.TestId == "tests/test_core.py::test_skipped" && !e.Enabled);
        Assert.Single(entries, e => e.TestId == "tests/test_core.py::test_cases[0]"
            && e.TheoryCaseValues.SequenceEqual(new[] { "a" }));
        Assert.Single(entries, e => e.TestId == "tests/test_core.py::test_cases[1]"
            && e.TheoryCaseValues.SequenceEqual(new[] { "b" }));
    }

    [Fact]
    public void TestCaseClassesAreLeftToUnittest()
    {
        var model = Parse("""
            import unittest

            class HelperTests(unittest.TestCase):
                def test_helper(self):
                    pass

            class PlainTests:
                def test_method(self):
                    pass
            """);
        var pytest = new PytestAdapter();
        var unittestAdapter = new UnittestAdapter();
        Assert.True(pytest.CanAdapt(model));
        Assert.True(unittestAdapter.CanAdapt(model));
        Assert.Equal("unittest", unittestAdapter.FrameworkId);

        // pytest adapter skips TestCase classes but keeps plain classes
        var pytestIds = pytest.ExtractEntries(model).Select(e => e.TestId).ToArray();
        Assert.Contains("tests/test_core.py::PlainTests::test_method", pytestIds);
        Assert.DoesNotContain("tests/test_core.py::HelperTests::test_helper", pytestIds);

        // unittest adapter owns TestCase methods only
        var unittestEntry = Assert.Single(unittestAdapter.ExtractEntries(model));
        Assert.Equal("tests/test_core.py::HelperTests::test_helper", unittestEntry.TestId);
    }

    [Fact]
    public void UnittestSkipDecoratorsDisableEntries()
    {
        var model = Parse("""
            import unittest

            class ClientTests(unittest.TestCase):
                @unittest.skipIf(False, "not ready")
                def test_active(self):
                    pass

                @unittest.skipUnless(False, "never")
                def test_blocked(self):
                    pass
            """);
        var entries = new UnittestAdapter().ExtractEntries(model);
        Assert.Equal(2, entries.Count);
        Assert.Single(entries, e => e.TestId.EndsWith("::test_active") && e.Enabled);
        Assert.Single(entries, e => e.TestId.EndsWith("::test_blocked") && !e.Enabled);
    }

    [Fact]
    public void RegistryMergesPytestAndUnittestInFixedOrder()
    {
        var model = Parse("""
            import unittest

            def test_module_level():
                pass

            class SuiteTests(unittest.TestCase):
                def test_case(self):
                    pass
            """);
        var entries = new PythonTestFrameworkRegistry().ExtractEntries(model);
        Assert.Equal(2, entries.Count);
        // pytest first (module function), then unittest (TestCase method)
        Assert.Equal("tests/test_core.py::test_module_level", entries[0].TestId);
        Assert.Equal("tests/test_core.py::SuiteTests::test_case", entries[1].TestId);
    }
}
