<?php

namespace Equiv\Tests;

use Equiv\LogStore;
use PHPUnit\Framework\TestCase;

final class LogStoreTest extends TestCase
{
    public function testAppendsLog(): void
    {
        $store = new LogStore();
        $store->appendLog("entry");
    }
}
