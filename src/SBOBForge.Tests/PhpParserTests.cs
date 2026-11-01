using System.Linq;
using SBOBForge.Core;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class PhpParserTests
{
    private static CodeModel Parse(string source) =>
        new PhpSourceParser().Parse("sut/php/ConfigStore.php", source);

    [Fact]
    public void NamespaceUseClassAndMethods()
    {
        var model = Parse("""
            <?php

            namespace Example;

            use PHPUnit\Framework\TestCase;

            class ConfigStore
            {
                public function loadConfig(): array
                {
                    $raw = file_get_contents("/etc/app/config.json");
                    return json_decode($raw, true);
                }

                private function saveCache(array $payload): void
                {
                    file_put_contents("/var/cache/last.json", $payload);
                }
            }
            """);
        var type = Assert.Single(model.Types);
        Assert.Equal("Example", type.Namespace);
        Assert.Equal("ConfigStore", type.Name);
        Assert.Equal(2, type.Methods.Count);

        var load = Assert.Single(type.Methods, m => m.Name == "loadConfig");
        Assert.Equal("Example.ConfigStore.loadConfig", load.QualifiedName);
        var read = Assert.Single(load.Invocations, i => i.Method == "file_get_contents");
        Assert.Null(read.Receiver);
        Assert.Equal("/etc/app/config.json", read.Args[0].Value);

        Assert.Equal(new[] { "PHPUnit\\Framework\\TestCase" }, (model.Imports ?? Array.Empty<string>()).ToArray());
    }

    [Fact]
    public void EntryCueAttributesFromDocblocksAndHashAttributes()
    {
        var model = Parse("""
            <?php

            class ConfigStoreTest
            {
                #[Test]
                public function savesCache(): void
                {
                    $this->markTestSkipped("not wired yet");
                }

                /**
                 * @test
                 */
                public function appendsLog(): void
                {
                    $this->appendLog("entry");
                }

                /**
                 * @requires extension pdo_mysql
                 */
                public function testRequiresDatabase(): void
                {
                    $this->loadConfig();
                }
            }
            """);
        var type = Assert.Single(model.Types);

        var saves = Assert.Single(type.Methods, m => m.Name == "savesCache");
        Assert.Contains(saves.Attributes, a => a.Name == "Test");
        var skip = Assert.Single(saves.Invocations, i => i.Method == "markTestSkipped");
        Assert.Equal("this", skip.Receiver);

        var appends = Assert.Single(type.Methods, m => m.Name == "appendsLog");
        Assert.Contains(appends.Attributes, a => a.Name == "@test");

        var requires = Assert.Single(type.Methods, m => m.Name == "testRequiresDatabase");
        Assert.Contains(requires.Attributes, a => a.Name == "@requires");
    }

    [Fact]
    public void StaticReceiversAndArgKinds()
    {
        var model = Parse("""
            <?php

            class ConfigStore
            {
                public function loadUser(string $user): array
                {
                    $raw = file_get_contents("/etc/app/users/" . $user . ".json");
                    return json_decode($raw, true);
                }

                public function runGitStatus(): array
                {
                    $output = [];
                    exec("git status", $output);
                    return $output;
                }

                public function config(): array
                {
                    return ConfigStore::loadDefaults();
                }
            }
            """);
        var type = Assert.Single(model.Types);

        // string concat with a $var is not a constant literal
        var load = Assert.Single(type.Methods, m => m.Name == "loadUser");
        var read = Assert.Single(load.Invocations, i => i.Method == "file_get_contents");
        Assert.Equal(ArgKind.Expression, read.Args[0].Kind);

        // positional args classify: literal first, $output an identifier
        var git = Assert.Single(type.Methods, m => m.Name == "runGitStatus");
        var exec = Assert.Single(git.Invocations, i => i.Method == "exec");
        Assert.Equal("git status", exec.Args[0].Value);
        Assert.Equal(ArgKind.Identifier, exec.Args[1].Kind);
        Assert.Equal("$output", exec.Args[1].Value);

        // Class::method() → static receiver chain
        var config = Assert.Single(type.Methods, m => m.Name == "config");
        var inv = Assert.Single(config.Invocations);
        Assert.Equal("ConfigStore", inv.Receiver);
        Assert.Equal("loadDefaults", inv.Method);
    }

    [Fact]
    public void CodeOutsidePhpTagsIsHtmlAndLoneFunctionsBecomeFileStemMethods()
    {
        var model = Parse("""
            <html><body>not code</body></html>
            <?php

            function helper(): void
            {
                echo "entry" . PHP_EOL;
            }
            """);
        var type = Assert.Single(model.Types);
        Assert.Equal("ConfigStore", type.Name); // file-stem pseudo-type
        Assert.Single(type.Methods, m => m.Name == "helper");
    }

    [Fact]
    public void UnbalancedBraceIsAHardError()
    {
        Assert.Throws<ParseHardError>(() => Parse("<?php\n\nclass A {\n    public function f() {\n"));
    }
}
