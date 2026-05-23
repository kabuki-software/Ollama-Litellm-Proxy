#Requires -RunAsAdministrator
<#
.SYNOPSIS
	Installs or uninstalls Ollama-Litellm-Proxy as a Windows service.

.DESCRIPTION
	Use -Install to register the service (set to start automatically).
	Use -Uninstall to stop and remove the service.

.PARAMETER Install
	Install the service.

.PARAMETER Uninstall
	Uninstall the service.

.PARAMETER ServiceName
	Name of the Windows service. Default: OllamaYarpProxy

.PARAMETER DisplayName
	Display name shown in Services. Default: Ollama YARP Proxy

.PARAMETER Description
	Service description. Default: Reverse proxy that exposes an Ollama-compatible endpoint.

.EXAMPLE
	.\service.ps1 -Install
	.\service.ps1 -Uninstall
#>

param(
	[switch]$Install,
	[switch]$Uninstall,
	[string]$ServiceName  = 'OllamaLitellmProxy',
	[string]$DisplayName  = 'Ollama LiteLLM Proxy',
	[string]$Description  = 'Reverse proxy that exposes an Ollama-compatible endpoint and redirects requests to a LiteLLM backend.'
)

$ExeName   = 'Ollama-Litellm-Proxy.exe'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$ExePath   = Join-Path $ScriptDir $ExeName

function Install-Service {
	if (-not (Test-Path $ExePath)) {
		Write-Error "Executable not found: $ExePath"
		exit 1
	}

	if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
		Write-Warning "Service '$ServiceName' already exists. Run with -Uninstall first to reinstall."
		exit 1
	}

	Write-Host "Installing service '$ServiceName'..."
	New-Service `
		-Name        $ServiceName `
		-BinaryPathName $ExePath `
		-DisplayName $DisplayName `
		-Description $Description `
		-StartupType Automatic | Out-Null

	Write-Host "Starting service '$ServiceName'..."
	Start-Service -Name $ServiceName

	$svc = Get-Service -Name $ServiceName
	Write-Host "Service status: $($svc.Status)"
	Write-Host "Installation complete."
}

function Uninstall-Service {
	$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
	if (-not $svc) {
		Write-Warning "Service '$ServiceName' not found. Nothing to uninstall."
		exit 0
	}

	if ($svc.Status -ne 'Stopped') {
		Write-Host "Stopping service '$ServiceName'..."
		Stop-Service -Name $ServiceName -Force
	}

	Write-Host "Removing service '$ServiceName'..."

	# Remove-Service can fail with "marked for deletion" when Services.msc or another
	# handle is open. sc.exe delete is more reliable in that situation.
	try {
		Remove-Service -Name $ServiceName -ErrorAction Stop
	}
	catch {
		Write-Warning "Remove-Service failed ($($_.Exception.Message)). Falling back to sc.exe delete..."
		$result = sc.exe delete $ServiceName
		if ($LASTEXITCODE -ne 0) {
			Write-Error "sc.exe delete failed: $result"
			Write-Host "Close Services.msc (services.msc) and any other tool that has the service open, then try again."
			exit 1
		}
	}

	Write-Host "Service '$ServiceName' removed successfully."
	Write-Host "Note: if Services.msc is open, close and reopen it to see the change take effect."
}

if ($Install -and $Uninstall) {
	Write-Error "Specify either -Install or -Uninstall, not both."
	exit 1
}

if ($Install) {
	Install-Service
}
elseif ($Uninstall) {
	Uninstall-Service
}
else {
	Write-Host "Usage:"
	Write-Host "  .\service.ps1 -Install    # install and start the service"
	Write-Host "  .\service.ps1 -Uninstall  # stop and remove the service"
	Write-Host ""
	Write-Host "Optional parameters: -ServiceName, -DisplayName, -Description"
}
