<#
.SYNOPSIS
    Prints a one-time invitation link for bibliotaph.online.

.DESCRIPTION
    Runs the server's own invite command inside the running container, so it works before anyone can sign in: it's how
    the first admin gets in. After that, admins make invitations in Settings.

.EXAMPLE
    ./infra/invite.ps1 -Admin -Note 'Dave'
#>
#Requires -Version 7.3
# Every setting is named, so a stray word, such as a ">>" pasted from a continued prompt, which PowerShell reads as a
# redirection, stops the script rather than becoming the next setting.
[CmdletBinding(PositionalBinding = $false)]
param(
    # Who it's for; only admins see it.
    [string] $Note,
    # The account it makes can invite others.
    [switch] $Admin,
    [string] $ResourceGroup = 'rg-bibliotaph',
    [string] $App = 'bibliotaph-app'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$command = 'bibliotaph-invite'
if ($Admin) { $command += ' --admin' }
# az containerapp exec splits the command on spaces, so the note can't carry any.
if ($Note) { $command += " --note $($Note -replace '\s+', '-')" }
az containerapp exec -n $App -g $ResourceGroup --command $command
