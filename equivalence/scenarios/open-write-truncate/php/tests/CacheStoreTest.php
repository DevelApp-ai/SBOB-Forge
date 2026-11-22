<?php

namespace Equiv\Tests;

use Equiv\CacheStore;
use PHPUnit\Framework\TestCase;

final class CacheStoreTest extends TestCase
{
    public function testSavesCache(): void
    {
        $store = new CacheStore();
        $store->saveCache("{}");
    }
}
