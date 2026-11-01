function Load-Config {
    $raw = Get-Content -Path "/etc/app/config.json" -Raw
    return ($raw | ConvertFrom-Json)
}

function Save-Cache {
    param($Payload)
    Set-Content -Path "%PackageRoot%/cache/last.json" -Value $Payload
}

function Add-LogEntry {
    Add-Content -Path "/var/log/ps-app.log" -Value "entry"
}

function Add-DynamicLog {
    param($Name)
    Add-Content -Path "/var/log/ps-$Name.log" -Value "entry"
}

function Invoke-GitStatus {
    & "git" "status"
}

function Start-GitStatus {
    Start-Process -FilePath "git" -ArgumentList "status" -NoNewWindow
}

function Get-Status {
    Invoke-WebRequest -Uri "https://api.example.com/status"
}

function Invoke-Shell {
    Invoke-Expression "git status"
}

function Remove-Cache {
    Remove-Item -Path "%PackageRoot%/cache/last.json"
}
