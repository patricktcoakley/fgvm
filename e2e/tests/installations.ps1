Set-StrictMode -Version Latest

Suite "installed versions" {
    Test "lists multiple seeded versions in text and json" {
        $stable = Add-FixtureInstallation "4.6.2-stable"
        $older = Add-FixtureInstallation "4.5-stable" -Default
        $mono = Add-FixtureInstallation "4.6.2-stable" "mono"

        $json = Run "list" "--json"
        $text = Run "list"

        Assert.ExitCode 0 $json "fgvm list --json"
        $installed = Json $json.Stdout
        Assert.Equal 3 $installed.Count
        Assert.ContainsAll @($installed.name) $stable.Name $older.Name $mono.Name
        $defaults = @($installed | Where-Object { $_.isDefault })
        Assert.Equal 1 $defaults.Count
        Assert.Equal $older.Name $defaults[0].name
        Assert.Equal $older.Name $installed[0].name

        Assert.ExitCode 0 $text "fgvm list"
        Assert.ContainsAll $text.Stdout $stable.Name $older.Name $mono.Name
        $textLines = @($text.Stdout -split "\r?\n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        Assert.Contains $older.Name $textLines[0]
    }

    Test "which reports the seeded default executable" {
        $seeded = Add-FixtureInstallation "4.6.2-stable" -Default

        $which = Run "which" "--json"

        Assert.ExitCode 0 $which "fgvm which --json"
        $view = Json $which.Stdout
        Assert.True $view.hasVersion
        Assert.Equal ([System.IO.Path]::GetFullPath($seeded.ExecutablePath)) ([System.IO.Path]::GetFullPath($view.executablePath))
    }
}
