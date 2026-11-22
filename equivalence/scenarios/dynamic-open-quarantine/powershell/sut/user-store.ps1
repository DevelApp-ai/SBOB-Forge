function Load-UserLog {
    param($User)
    $raw = Get-Content -Path "/var/log/$User.log" -Raw
    return $raw
}

function Add-AppLogEntry {
    param($Entry)
    Add-Content -Path "/var/log/app.log" -Value $Entry
}
