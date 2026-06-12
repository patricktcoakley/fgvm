<#
.SYNOPSIS
    Assert that a condition is true.
.PARAMETER Condition
    The boolean value to test.
.PARAMETER Message
    Optional failure message.
#>
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

<#
.SYNOPSIS
    Assert that a condition is false.
.PARAMETER Condition
    The boolean value to test.
.PARAMETER Message
    Optional failure message.
#>
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

<#
.SYNOPSIS
    Assert that two values are equal (deep comparison for collections).
.PARAMETER Expected
    Expected value.
.PARAMETER Actual
    Actual value.
.PARAMETER Message
    Optional failure message.
#>
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

<#
.SYNOPSIS
    Assert that two values are NOT equal.
.PARAMETER Expected
    Value the actual should differ from.
.PARAMETER Actual
    Actual value.
.PARAMETER Message
    Optional failure message.
#>
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

<#
.SYNOPSIS
    Assert that a container (string or collection) contains an expected value.
.DESCRIPTION
    For strings, uses ordinal substring search. For collections, uses
    object-equality on each element.
.PARAMETER Expected
    The value to search for (substring for strings, element for collections).
.PARAMETER Actual
    The container string or collection.
.PARAMETER Message
    Optional failure message.
#>
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

<#
.SYNOPSIS
    Assert that a container does NOT contain an expected value.
.PARAMETER Expected
    The value that should be absent.
.PARAMETER Actual
    The container string or collection.
.PARAMETER Message
    Optional failure message.
#>
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

<#
.SYNOPSIS
    Assert that a container contains ALL of the given values.
.PARAMETER Actual
    The container string or collection.
.PARAMETER Expected
    One or more values that must all be present.
.PARAMETER Message
    Optional failure message. Pass as a named parameter before the expected values:
    Assert.ContainsAll $actual -Message "msg" "val1" "val2"
#>
function Assert.ContainsAll {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [object] $Actual,

        [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true, Position = 1)]
        [object[]] $Expected,

        [string] $Message = ""
    )

    $missing = @($Expected | Where-Object { -not (Assert.ContainsValue $_ $Actual) })
    if ($missing.Count -gt 0) {
        throw (Assert.FormatMessage $Message "Expected value to contain all entries. Missing: $($missing -join ', ').")
    }
}

<#
.SYNOPSIS
    Assert that a value (string or collection) is empty.
.PARAMETER Actual
    The value to check. Null, empty string, and empty collections are empty.
.PARAMETER Message
    Optional failure message.
#>
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

<#
.SYNOPSIS
    Assert that a value (string or collection) is NOT empty.
.PARAMETER Actual
    The value to check.
.PARAMETER Message
    Optional failure message.
#>
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

<#
.SYNOPSIS
    Assert that a process result has the expected exit code.
.DESCRIPTION
    On failure the error message includes the full stdout and stderr output.
.PARAMETER Expected
    Expected exit code.
.PARAMETER Result
    The pscustomobject returned by Run or InvokeProcess (must have ExitCode,
    Stdout, and Stderr properties).
.PARAMETER Message
    Optional failure message.
#>
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

<#
.SYNOPSIS
    Check whether an expected value is contained within a string or collection.
.PARAMETER Expected
    Value to search for.
.PARAMETER Actual
    String or collection to search within.
#>
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

<#
.SYNOPSIS
    Deep equality comparison supporting nulls, strings, and collections.
.PARAMETER Expected
    Expected value.
.PARAMETER Actual
    Actual value.
#>
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

<#
.SYNOPSIS
    Count the number of elements in a value, handling null, strings, and collections.
#>
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

<#
.SYNOPSIS
    Format an assertion error message, combining optional user message with default.
#>
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

<#
.SYNOPSIS
    Format a detailed error message for process exit-code failures.
.DESCRIPTION
    Includes exit code, stdout, and stderr in the output for debugging.
#>
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
