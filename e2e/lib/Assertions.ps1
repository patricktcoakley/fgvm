function Assert.True {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [bool] $Condition,

        [Parameter(Position = 1)]
        [string] $Message = "Expected condition to be true."
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert.False {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [bool] $Condition,

        [Parameter(Position = 1)]
        [string] $Message = "Expected condition to be false."
    )

    if ($Condition) {
        throw $Message
    }
}

function Assert.Equal {
    param(
        [Parameter(Position = 0)]
        [AllowNull()]
        [object] $Expected,

        [Parameter(Position = 1)]
        [AllowNull()]
        [object] $Actual,

        [Parameter(Position = 2)]
        [string] $Message = ""
    )

    if (-not (Assert.ValuesEqual $Expected $Actual)) {
        throw (Assert.FormatMessage $Message "Expected '$Expected', got '$Actual'.")
    }
}

function Assert.NotEqual {
    param(
        [Parameter(Position = 0)]
        [AllowNull()]
        [object] $Expected,

        [Parameter(Position = 1)]
        [AllowNull()]
        [object] $Actual,

        [Parameter(Position = 2)]
        [string] $Message = ""
    )

    if (Assert.ValuesEqual $Expected $Actual) {
        throw (Assert.FormatMessage $Message "Expected value to differ from '$Expected'.")
    }
}

function Assert.Contains {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [object] $Expected,

        [Parameter(Mandatory = $true, Position = 1)]
        [object] $Actual,

        [Parameter(Position = 2)]
        [string] $Message = ""
    )

    if (-not (Assert.ContainsValue $Expected $Actual)) {
        throw (Assert.FormatMessage $Message "Expected '$Actual' to contain '$Expected'.")
    }
}

function Assert.NotContains {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [object] $Expected,

        [Parameter(Mandatory = $true, Position = 1)]
        [object] $Actual,

        [Parameter(Position = 2)]
        [string] $Message = ""
    )

    if (Assert.ContainsValue $Expected $Actual) {
        throw (Assert.FormatMessage $Message "Expected '$Actual' not to contain '$Expected'.")
    }
}

function Assert.ContainsAll {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [object] $Actual,

        [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true, Position = 1)]
        [object[]] $Expected
    )

    $missing = @($Expected | Where-Object { -not (Assert.ContainsValue $_ $Actual) })
    if ($missing.Count -gt 0) {
        throw "Expected value to contain all entries. Missing: $($missing -join ', ')."
    }
}

function Assert.Empty {
    param(
        [Parameter(Position = 0)]
        [AllowNull()]
        [object] $Actual,

        [Parameter(Position = 1)]
        [string] $Message = ""
    )

    $count = Assert.Count $Actual
    if ($count -ne 0) {
        throw (Assert.FormatMessage $Message "Expected empty value, got count $count.")
    }
}

function Assert.NotEmpty {
    param(
        [Parameter(Position = 0)]
        [AllowNull()]
        [object] $Actual,

        [Parameter(Position = 1)]
        [string] $Message = ""
    )

    if ((Assert.Count $Actual) -eq 0) {
        throw (Assert.FormatMessage $Message "Expected non-empty value.")
    }
}

function Assert.ExitCode {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [int] $Expected,

        [Parameter(Mandatory = $true, Position = 1)]
        [pscustomobject] $Result,

        [Parameter(Position = 2)]
        [string] $Message = ""
    )

    if ($Result.ExitCode -ne $Expected) {
        throw (Assert.FormatCommandMessage $Result (Assert.FormatMessage $Message "Expected exit code $Expected, got $($Result.ExitCode)."))
    }
}

function Assert.ContainsValue {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [object] $Expected,

        [Parameter(Mandatory = $true, Position = 1)]
        [object] $Actual
    )

    if ($Actual -is [string]) {
        return $Actual.IndexOf([string] $Expected, [System.StringComparison]::Ordinal) -ge 0
    }

    foreach ($item in @($Actual)) {
        if ([object]::Equals($Expected, $item)) {
            return $true
        }
    }

    return $false
}

function Assert.ValuesEqual {
    param(
        [Parameter(Position = 0)]
        [AllowNull()]
        [object] $Expected,

        [Parameter(Position = 1)]
        [AllowNull()]
        [object] $Actual
    )

    if ($null -eq $Expected -or $null -eq $Actual) {
        return $null -eq $Expected -and $null -eq $Actual
    }

    if ($Expected -is [string] -or $Actual -is [string]) {
        return [object]::Equals($Expected, $Actual)
    }

    if ($Expected -is [System.Collections.IEnumerable] -and $Actual -is [System.Collections.IEnumerable]) {
        $expectedItems = @($Expected)
        $actualItems = @($Actual)
        if ($expectedItems.Count -ne $actualItems.Count) {
            return $false
        }

        for ($i = 0; $i -lt $expectedItems.Count; $i++) {
            if (-not (Assert.ValuesEqual $expectedItems[$i] $actualItems[$i])) {
                return $false
            }
        }

        return $true
    }

    return [object]::Equals($Expected, $Actual)
}

function Assert.Count {
    param(
        [Parameter(Position = 0)]
        [AllowNull()]
        [object] $Actual
    )

    if ($null -eq $Actual) {
        return 0
    }

    if ($Actual -is [string]) {
        return $Actual.Length
    }

    if ($Actual -is [System.Collections.ICollection]) {
        return $Actual.Count
    }

    return @($Actual).Count
}

function Assert.FormatMessage {
    param(
        [Parameter(Position = 0)]
        [string] $Message,

        [Parameter(Mandatory = $true, Position = 1)]
        [string] $DefaultMessage
    )

    if ([string]::IsNullOrWhiteSpace($Message)) {
        return $DefaultMessage
    }

    return "$Message $DefaultMessage"
}

function Assert.FormatCommandMessage {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [pscustomobject] $Result,

        [Parameter(Mandatory = $true, Position = 1)]
        [string] $Message
    )

    @"
$Message
Exit code: $($Result.ExitCode)
STDOUT:
$($Result.Stdout)
STDERR:
$($Result.Stderr)
"@
}
