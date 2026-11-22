<?php

namespace Equiv;

class ConfigStore
{
    public function loadConfig(): string
    {
        return file_get_contents("/etc/app/config.json");
    }
}
