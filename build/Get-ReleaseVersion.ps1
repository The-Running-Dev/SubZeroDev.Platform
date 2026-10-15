#Requires -Version 7.0
<#
.SYNOPSIS
    Returns the package version of a release run: its tag without the leading v (S9.6).

.DESCRIPTION
    The release workflow (.github/workflows/release.yml) runs on a version tag, pushed or picked as
    the ref of a manual run. The published version is 0.x -- the API stays explicitly unstable --
    so the script fails, before anything is packed or published, on a run that is not on a tag or
    whose tag is not v0.x.y. It is the pre-build script of the dotnet job there, and its other
    scripts read the version through it.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

if ($env:GITHUB_REF -notlike 'refs/tags/*') {
    throw "A release runs on a version tag, not '$env:GITHUB_REF'. Push the tag, or pick it as the ref of a manual run."
}

$tag = $env:GITHUB_REF_NAME
if ($tag -notmatch '^v(?<version>0\.\d+\.\d+)$') {
    throw "'$tag' is not a 0.x version -- the API stays explicitly unstable (S9.6)."
}

$Matches['version']
