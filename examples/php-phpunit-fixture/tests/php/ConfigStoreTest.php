<?php

namespace Example\Tests;

use Example\ConfigStore;
use PHPUnit\Framework\TestCase;

final class ConfigStoreTest extends TestCase
{
    public function testLoadsConfig(): void
    {
        $store = new ConfigStore();
        $store->loadConfig();
    }

    #[Test]
    public function savesCache(): void
    {
        $store = new ConfigStore();
        $store->saveCache(["value" => 42]);
    }

    /**
     * @test
     */
    public function appendsLog(): void
    {
        $store = new ConfigStore();
        $store->appendLog("entry");
    }

    public function testRunsGitStatus(): void
    {
        $store = new ConfigStore();
        $store->runGitStatus();
    }

    public function testLegacyPath(): void
    {
        $this->markTestSkipped("legacy path is retired");
        file_get_contents("/etc/app/legacy.json");
    }

    /**
     * @requires extension pdo_mysql
     */
    public function testRequiresDatabase(): void
    {
        $store = new ConfigStore();
        $store->loadConfig();
    }
}
