<?php

namespace Equiv;

class CacheStore
{
    public function saveCache(string $payload): void
    {
        file_put_contents("/var/lib/app/cache.json", $payload);
    }
}
