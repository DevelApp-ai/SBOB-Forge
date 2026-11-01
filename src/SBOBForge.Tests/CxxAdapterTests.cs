using System.Linq;
using SBOBForge.Adapters.Cxx;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class CxxAdapterTests
{
    // ---- GoogleTest ---------------------------------------------------------

    [Fact]
    public void GoogleTestMacroBlocksProduceSuiteDotNameEntries()
    {
        var model = new C17SourceParser().Parse("tests/c/store_test.c", """
            #include "store.h"

            TEST(Store, OpensConfigReadOnly)
            {
                FILE *f = fopen("/etc/app/config.json", "r");
                ASSERT_NE(f, NULL);
            }

            TEST_F(StoreFixture, WritesCache)
            {
                write_cache();
                GTEST_SKIP();
            }
            """);
        var adapter = new GoogleTestAdapter();
        Assert.True(adapter.CanAdapt(model));
        Assert.Equal("googletest", adapter.FrameworkId);

        var entries = adapter.ExtractEntries(model);
        Assert.Equal(2, entries.Count);
        Assert.Equal("tests/c/store_test.c::Store.OpensConfigReadOnly", entries[0].TestId);
        Assert.True(entries[0].Enabled);
        // TEST_F: suite = fixture name
        Assert.Equal("tests/c/store_test.c::StoreFixture.WritesCache", entries[1].TestId);
        // GTEST_SKIP() in the body disables the entry (TDS 7.2)
        Assert.False(entries[1].Enabled);
    }

    [Fact]
    public void GoogleTestDoesNotAdaptCatch2OrUnityFiles()
    {
        var model = new Cpp20SourceParser().Parse("tests/cpp/store_test.cpp", """
            TEST_CASE("writes and reads")
            {
                std::system("write");
            }
            """);
        Assert.False(new GoogleTestAdapter().CanAdapt(model));
    }

    // ---- Catch2 -------------------------------------------------------------

    [Fact]
    public void Catch2EntriesUseFirstStringLiteralTitle()
    {
        var model = new Cpp20SourceParser().Parse("tests/cpp/store_test.cpp", """
            #include <catch2/catch_test_macros.hpp>

            TEST_CASE("writes and reads", "[store]")
            {
                SECTION("write")
                {
                    std::system("write");
                }
            }

            TEST_CASE_METHOD(StoreFixture, "purges cache")
            {
                SKIP("not on this platform");
            }
            """);
        var adapter = new Catch2Adapter();
        Assert.True(adapter.CanAdapt(model));
        Assert.Equal("catch2", adapter.FrameworkId);

        var entries = adapter.ExtractEntries(model);
        Assert.Equal(2, entries.Count);
        Assert.Equal("tests/cpp/store_test.cpp::writes and reads", entries[0].TestId);
        Assert.True(entries[0].Enabled);
        // fixture form: the title is macro argument 1, not the fixture name
        Assert.Equal("tests/cpp/store_test.cpp::purges cache", entries[1].TestId);
        Assert.False(entries[1].Enabled); // SKIP() in the body
    }

    [Fact]
    public void Catch2DoesNotAdaptGoogleTestFiles()
    {
        var model = new Cpp20SourceParser().Parse("tests/cpp/store_test.cpp", """
            TEST(Store, OpensConfigReadOnly)
            {
                std::system("read");
            }
            """);
        Assert.False(new Catch2Adapter().CanAdapt(model));
    }

    // ---- Unity ---------------------------------------------------------------

    [Fact]
    public void UnityEntriesRequireRunTestNaming()
    {
        var model = new C17SourceParser().Parse("tests/c/unity_test.c", """
            #include "unity.h"

            void test_opens_config(void)
            {
                FILE *f = fopen("/etc/app/config.json", "r");
                TEST_ASSERT_NOT_NULL(f);
            }

            void test_writes_cache(void)
            {
                TEST_IGNORE();
            }

            void test_helper_not_run(void)
            {
                system("false");
            }

            int main(void)
            {
                UNITY_BEGIN();
                RUN_TEST(test_opens_config);
                RUN_TEST(test_writes_cache);
                return UNITY_END();
            }
            """);
        var adapter = new UnityAdapter();
        Assert.True(adapter.CanAdapt(model));
        Assert.Equal("unity", adapter.FrameworkId);

        var entries = adapter.ExtractEntries(model);
        // CTest runner macro handling (TDS 5.2 M5): only RUN_TEST-named functions are entries
        Assert.Equal(2, entries.Count);
        Assert.Equal("tests/c/unity_test.c::test_opens_config", entries[0].TestId);
        Assert.True(entries[0].Enabled);
        Assert.Equal("tests/c/unity_test.c::test_writes_cache", entries[1].TestId);
        Assert.False(entries[1].Enabled); // TEST_IGNORE() in the body
    }

    [Fact]
    public void UnityDoesNotAdaptFilesWithoutRunTest()
    {
        var model = new C17SourceParser().Parse("tests/c/unity_test.c", """
            void test_opens_config(void)
            {
                fopen("/etc/app/config.json", "r");
            }
            """);
        Assert.False(new UnityAdapter().CanAdapt(model));
        Assert.Empty(new UnityAdapter().ExtractEntries(model));
    }

    // ---- registry ------------------------------------------------------------

    [Fact]
    public void RegistryAdaptsInFixedOrderAndMergesEntries()
    {
        var model = new Cpp20SourceParser().Parse("tests/cpp/store_test.cpp", """
            TEST(Store, GtestEntry)
            {
                std::system("read");
            }

            TEST_CASE("Catch2 entry")
            {
                std::system("write");
            }
            """);
        var registry = new CxxTestFrameworkRegistry();
        var detected = registry.Detect(model);
        Assert.Equal(new[] { "googletest", "catch2" },
            detected.Select(a => a.FrameworkId).ToArray());
        Assert.Equal(2, registry.ExtractEntries(model).Count);
    }
}
