function Load-Config {
    $raw = Get-Content -Path "/etc/app/config.json" -Raw
    return $raw
}
