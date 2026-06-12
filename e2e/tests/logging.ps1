Set-StrictMode -Version Latest

Suite "logging" {
    Test "returns parseable logs for previous operations" {
        Run "list" | Out-Null
        Run "search" "--json" "4.6" | Out-Null
        Run "which" | Out-Null

        $logs = Run "logs" "--json"

        Assert.ExitCode 0 $logs "fgvm logs --json"
        $entries = @(Json $logs.Stdout)
        Assert.NotEmpty $entries
        Assert.True (@($entries | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Message) }).Count -gt 0) "Logs should contain messages."
    }

    Test "filters logs by level and message" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $set = Run "set" "4.6"
        Assert.ExitCode 0 $set "fgvm set 4.6"

        $byLevel = Run "logs" "--json" "--level" "info"
        $byMessage = Run "logs" "--json" "--message" "Successfully set version"

        Assert.ExitCode 0 $byLevel "fgvm logs --level info"
        $levelEntries = @(Json $byLevel.Stdout)
        Assert.NotEmpty $levelEntries
        Assert.Empty @($levelEntries | Where-Object { $_.LogLevel -notlike "*INFORMATION*" })

        Assert.ExitCode 0 $byMessage "fgvm logs --message"
        $messageEntries = @(Json $byMessage.Stdout)
        Assert.NotEmpty $messageEntries
        Assert.Empty @($messageEntries | Where-Object { $_.Message -notlike "*Successfully set version*" })
    }

    Test "reports malformed log entries without failing" {
        Run "list" | Out-Null
        Add-Content -LiteralPath $Context.LogPath -Value "not-json"

        $logs = Run "logs"

        Assert.ExitCode 0 $logs "fgvm logs with malformed entry"
        Assert.Contains "Skipped 1 malformed log entries" $logs.Stdout
    }
}
