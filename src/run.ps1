#!/usr/bin/env pwsh
# run.ps1 — Builds and keeps the selected proxy project running.
# Restarts automatically on crash; press Ctrl+C to stop.
#
# Usage:
#   .\run.ps1                          # runs OllamaLiteLLMProxy (default)
#   .\run.ps1 -Project LiteLLM        # runs Ollama-Litellm-Proxy
#   .\run.ps1 -Environment Development

param(
	[ValidateSet("OllamaLiteLLM", "LiteLLM")]
	[string]$Project = "OllamaLiteLLM",

	[string]$Environment = "Production"
)

$projectMap = @{
	"OllamaLiteLLM" = "OllamaLiteLLMProxy"
	"LiteLLM"    = "Ollama-Litellm-Proxy"
}

$projectDir  = Join-Path $PSScriptRoot $projectMap[$Project]
$displayName = $projectMap[$Project]
$ErrorActionPreference = "Stop"

Write-Host "Building $displayName..." -ForegroundColor Cyan
dotnet build $projectDir -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) {
	Write-Error "Build failed. Fix errors before running."
	exit 1
}

Write-Host "Starting $displayName (ASPNETCORE_ENVIRONMENT=$Environment). Press Ctrl+C to stop." -ForegroundColor Green

$restartDelay = 3   # seconds to wait between restarts

while ($true) {
	try {
		$env:ASPNETCORE_ENVIRONMENT = $Environment
		dotnet run --project $projectDir -c Release --no-build --nologo
		$exitCode = $LASTEXITCODE
	}
	catch {
		$exitCode = 1
	}

	# A clean Ctrl+C sets exit code 130 on Unix or raises a termination signal;
	# on Windows the pipeline break propagates here. Either way, stop looping.
	if ($exitCode -eq 0 -or [Console]::TreatControlCAsInput -eq $false -and $exitCode -eq 130) {
		Write-Host "Process exited cleanly." -ForegroundColor Yellow
		break
	}

	Write-Host "Process exited with code $exitCode. Restarting in $restartDelay seconds... (Ctrl+C to abort)" -ForegroundColor Red
	Start-Sleep -Seconds $restartDelay
}

