<?php

namespace Equiv;

class UserStore
{
    public function loadUser(string $user): string
    {
        return file_get_contents("/var/log/" . $user . ".log");
    }

    public function appendLog(string $entry): void
    {
        $handle = fopen("/var/log/app.log", "a");
        fwrite($handle, $entry);
        fclose($handle);
    }
}
