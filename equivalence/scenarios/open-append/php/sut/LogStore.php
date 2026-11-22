<?php

namespace Equiv;

class LogStore
{
    public function appendLog(string $entry): void
    {
        $handle = fopen("/var/log/app.log", "a");
        fwrite($handle, $entry);
        fclose($handle);
    }
}
