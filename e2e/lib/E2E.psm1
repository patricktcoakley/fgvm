<#
.SYNOPSIS
    E2E test framework module for the fgvm CLI.
.DESCRIPTION
    Provides the test runner, assertion library, process invocation, fixture
    management, and context isolation used by e2e tests in e2e/tests/.
#>

Set-StrictMode -Version Latest
$InformationPreference = "Continue"
$script:LibraryRoot = $PSScriptRoot

. (Join-Path $PSScriptRoot "Context.ps1")
. (Join-Path $PSScriptRoot "Process.ps1")
. (Join-Path $PSScriptRoot "Suite.ps1")
. (Join-Path $PSScriptRoot "Files.ps1")
. (Join-Path $PSScriptRoot "Assertions.ps1")
. (Join-Path $PSScriptRoot "Exports.ps1")
