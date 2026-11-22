<?php

namespace Equiv\Tests;

use Equiv\ConfigStore;
use PHPUnit\Framework\TestCase;

final class ConfigStoreTest extends TestCase
{
    public function testLoadsConfig(): void
    {
        $store = new ConfigStore();
        $store->loadConfig();
    }
}
