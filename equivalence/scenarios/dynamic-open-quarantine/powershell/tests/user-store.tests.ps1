Describe "user store" {
    It "loads user log" {
        Load-UserLog "alice"
    }

    It "appends app log" {
        Add-AppLogEntry "entry"
    }
}
