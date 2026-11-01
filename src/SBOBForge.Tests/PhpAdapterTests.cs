using System.Linq;
using SBOBForge.Adapters.Php;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class PhpAdapterTests
{
    private static CodeModel Parse(string source) =>
        new PhpSourceParser().Parse("tests/php/ConfigStoreTest.php", source);

    [Fact]
    public void AllThreeEntryCuesProduceEntries()
    {
        var model = Parse("""
            <?php

            namespace Example\Tests;

            use PHPUnit\Framework\TestCase;

            final class ConfigStoreTest extends TestCase
            {
                public function testLoadsConfig(): void
                {
                    $store->loadConfig();
                }

                #[Test]
                public function savesCache(): void
                {
                    $store->saveCache([]);
                }

                /**
                 * @test
                 */
                public function appendsLog(): void
                {
                    $store->appendLog("entry");
                }
            }
            """);
        var adapter = new PhpUnitAdapter();
        Assert.True(adapter.CanAdapt(model));
        Assert.Equal("phpunit", adapter.FrameworkId);

        var entries = adapter.ExtractEntries(model);
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.True(e.Enabled));
        Assert.Contains(entries, e => e.TestId == "tests/php/ConfigStoreTest.php::ConfigStoreTest::testLoadsConfig");
        Assert.Contains(entries, e => e.TestId.EndsWith("::ConfigStoreTest::savesCache"));
        Assert.Contains(entries, e => e.TestId.EndsWith("::ConfigStoreTest::appendsLog"));
    }

    [Fact]
    public void MarkTestSkippedAndRequiresDisableEntries()
    {
        var model = Parse("""
            <?php

            final class ConfigStoreTest extends \PHPUnit\Framework\TestCase
            {
                public function testLegacyPath(): void
                {
                    $this->markTestSkipped("retired");
                    file_get_contents("/etc/app/legacy.json");
                }

                public function testIncomplete(): void
                {
                    $this->markTestIncomplete("todo");
                }

                /**
                 * @requires extension pdo_mysql
                 */
                public function testRequiresDatabase(): void
                {
                    $store->loadConfig();
                }
            }
            """);
        var entries = new PhpTestFrameworkRegistry().ExtractEntries(model);
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.False(e.Enabled));
        Assert.All(entries, e => Assert.Equal("phpunit",
            Assert.Single(new PhpTestFrameworkRegistry().Detect(model)).FrameworkId));
    }

    [Fact]
    public void TestCaseBaseTypeAloneAdapts()
    {
        var model = Parse("""
            <?php

            final class CacheTest extends TestCase
            {
                public function helper(): void
                {
                    $this->assertNotNull(1);
                }
            }
            """);
        // no entry-cue method, but the class extends TestCase → PHPUnit file
        var registry = new PhpTestFrameworkRegistry();
        Assert.Single(registry.Detect(model));
        // no methods with an entry cue → no entries extracted
        Assert.Empty(registry.ExtractEntries(model));
    }
}
