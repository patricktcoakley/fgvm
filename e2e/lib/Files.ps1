function Json {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Text
    )

    try {
        $value = $Text | ConvertFrom-Json -NoEnumerate
    }
    catch {
        throw "Failed to parse JSON. $($_.Exception.Message)`n$Text"
    }

    Write-Output -NoEnumerate $value
}

function File.Exists {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Path
    )

    Test-Path -LiteralPath $Path -PathType Leaf
}

function File.Read {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Path
    )

    Get-Content -LiteralPath $Path -Raw
}

function File.ModifiedAt {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Path
    )

    (Get-Item -LiteralPath $Path).LastWriteTimeUtc
}

function File.SetModifiedAt {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Path,

        [Parameter(Mandatory = $true, Position = 1)]
        [datetime] $ModifiedAt
    )

    (Get-Item -LiteralPath $Path).LastWriteTimeUtc = $ModifiedAt
}

function Manifest.From {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Path
    )

    try {
        File.Read $Path | ConvertFrom-Json -AsHashtable
    }
    catch {
        throw "Failed to parse manifest at $Path. $($_.Exception.Message)"
    }
}

function Manifest.Write {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Path,

        [Parameter(Mandatory = $true, Position = 1)]
        [object] $Manifest,

        [Parameter(Position = 2)]
        [Nullable[datetime]] $ModifiedAt = $null
    )

    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    Set-Content -LiteralPath $Path -Value ($Manifest | ConvertTo-Json -Depth 10 -Compress) -NoNewline

    if ($null -ne $ModifiedAt) {
        File.SetModifiedAt $Path $ModifiedAt
    }
}
