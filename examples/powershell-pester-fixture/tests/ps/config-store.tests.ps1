Describe "config store" {
    It "loads config" {
        Load-Config
    }

    It "saves cache" {
        Save-Cache "payload"
    }

    It "appends log" {
        Add-LogEntry
    }

    It "runs git status" {
        Invoke-GitStatus
    }

    It "legacy path" -Skip {
        Add-Content -Path "/var/log/legacy.log" -Value "x"
    }
}
