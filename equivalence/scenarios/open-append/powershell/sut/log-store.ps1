function Add-LogEntry {
    param($Entry)
    Add-Content -Path "/var/log/app.log" -Value $Entry
}
