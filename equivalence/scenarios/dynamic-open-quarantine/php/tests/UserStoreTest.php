<?php

namespace Equiv\Tests;

use Equiv\UserStore;
use PHPUnit\Framework\TestCase;

final class UserStoreTest extends TestCase
{
    public function testLoadsUser(): void
    {
        $store = new UserStore();
        $store->loadUser("alice");
    }

    public function testAppendsLog(): void
    {
        $store = new UserStore();
        $store->appendLog("entry");
    }
}
