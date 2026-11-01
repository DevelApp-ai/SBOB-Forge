<?php

namespace Example;

class ConfigStore
{
    public function loadConfig(): array
    {
        $raw = file_get_contents("/etc/app/config.json");
        return json_decode($raw, true);
    }

    public function saveCache(array $payload): void
    {
        file_put_contents("%PackageRoot%/cache/last.json", json_encode($payload));
    }

    public function loadUser(string $user): array
    {
        $raw = file_get_contents("/etc/app/users/" . $user . ".json");
        return json_decode($raw, true);
    }

    public function appendLog(string $line): void
    {
        $handle = fopen("/var/log/php-app.log", "a");
        fwrite($handle, $line . "\n");
        fclose($handle);
    }

    public function runGitStatus(): array
    {
        $output = [];
        exec("git status", $output);
        return $output;
    }

    public function fetchStatus(): string
    {
        $curl = curl_init("https://api.example.com/status");
        curl_setopt($curl, CURLOPT_RETURNTRANSFER, true);
        $body = curl_exec($curl);
        curl_close($curl);
        return $body;
    }

    public function cleanCache(): void
    {
        unlink("%PackageRoot%/cache/last.json");
    }
}
