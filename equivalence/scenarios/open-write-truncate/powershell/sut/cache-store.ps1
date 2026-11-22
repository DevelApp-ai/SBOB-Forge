function Save-Cache {
    param($Payload)
    Set-Content -Path "/var/lib/app/cache.json" -Value $Payload
}
