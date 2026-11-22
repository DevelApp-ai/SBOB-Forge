<?php

namespace Equiv\Tests;

use Equiv\StatusClient;
use PHPUnit\Framework\TestCase;

final class StatusClientTest extends TestCase
{
    public function testFetchesStatus(): void
    {
        $client = new StatusClient();
        $client->fetchStatus();
    }
}
