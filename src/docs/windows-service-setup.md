# Installing Ollama-LiteLLM-Proxy as a Windows Service

Running the proxy as a Windows service means it starts automatically with Windows and keeps running in the background without a logged-in user or open terminal. This is the recommended setup for a permanent installation.

---

## Prerequisites

- Windows 10 / Windows Server 2016 or later
- [.NET 9 Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (or SDK) installed on the machine
- Administrator rights
- The project published or built in **Release** configuration (see [Build](#build))

---

## Build

From the repository root, publish a self-contained executable to a stable output folder:

```powershell
dotnet publish src/Ollama-Litellm-Proxy `
	-c Release `
	-r win-x64 `
	--self-contained true `
	-o C:\Services\OllamaLitellmProxy
```

> If you prefer a framework-dependent build (smaller, requires .NET 9 installed separately), omit `--self-contained true`.

The `service.ps1` script is automatically copied to the output folder by the build.

---

## Configure Before Installing

Edit `appsettings.json` in the output folder **before** installing the service so the correct settings are active at first start:

```json
"Clusters": {
  "ollamaCluster": {
	"Destinations": {
	  "destination1": {
		"Address": "http://<your-litellm-host>:4000/"
	  }
	}
  }
},
...
"Transforms": [
  { "RequestHeader": "Authorization", "Set": "Bearer <your-litellm-key>" }
]
```

---

## Install the Service

Open PowerShell **as Administrator**, navigate to the output folder, and run:

```powershell
cd C:\Services\OllamaLitellmProxy
.\service.ps1 -Install
```

The script will:
1. Register `Ollama-LiteLLM-Proxy.exe` as a Windows service named **`OllamaLitellmProxy`**
2. Set the startup type to **Automatic**
3. Start the service immediately

Verify it is running:

```powershell
Get-Service OllamaLitellmProxy
```

Expected output:

```
Status   Name                DisplayName
------   ----                -----------
Running  OllamaLitellmProxy  Ollama LiteLLM Proxy
```

---

## Optional Parameters

You can override the service name and display name if you need multiple instances or a custom name:

```powershell
.\service.ps1 -Install `
	-ServiceName  "OllamaLitellmProxyDev" `
	-DisplayName  "Ollama LiteLLM Proxy (Dev)" `
	-Description  "Dev instance pointing at staging LiteLLM"
```

---

## Managing the Service

Once installed, use standard Windows tools:

```powershell
# Stop the service
Stop-Service OllamaLitellmProxy

# Start the service
Start-Service OllamaLitellmProxy

# Restart after a configuration change
Restart-Service OllamaLitellmProxy

# Check status
Get-Service OllamaLitellmProxy
```

You can also manage it from **Services** (`services.msc`) or **Task Manager → Services**.

---

## Updating the Proxy

1. Stop the service:
   ```powershell
   Stop-Service OllamaLitellmProxy
   ```
2. Publish the new build to the same output folder:
   ```powershell
   dotnet publish src/Ollama-Litellm-Proxy -c Release -r win-x64 --self-contained true -o C:\Services\OllamaLitellmProxy
   ```
3. Start the service again:
   ```powershell
   Start-Service OllamaLitellmProxy
   ```

> You do **not** need to uninstall and reinstall the service for updates — only the binaries need to be replaced.

---

## Uninstall the Service

```powershell
cd C:\Services\OllamaLitellmProxy
.\service.ps1 -Uninstall
```

This stops the service (if running) and removes it from the Windows service registry. The files in the output folder are left untouched.

---

## Logging

By default the proxy logs to the console/stdout. When running as a service, Windows captures this output in the **Application** Event Log under the source `Ollama-LiteLLM-Proxy`.

To view recent log entries:

```powershell
Get-EventLog -LogName Application -Source "Ollama-LiteLLM-Proxy" -Newest 50
```

Or open **Event Viewer → Windows Logs → Application** and filter by source.

To write structured logs to a file, add a file sink to `appsettings.json` (e.g. via Serilog) or redirect stdout using the `Service` wrapper's `redirect` feature.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Service fails to start | Executable not found at registered path | Ensure the output folder path matches where `service.ps1` was run from |
| Service starts then stops immediately | Configuration error in `appsettings.json` | Check the Application Event Log for startup exceptions |
| Port 11434 already in use | Local Ollama or another proxy instance running | Change `Kestrel → Endpoints → Http → Url` in `appsettings.json` |
| HTTP 401 from LiteLLM | Wrong API key | Update `Bearer` token in `appsettings.json`, then restart the service |
| `Remove-Service` not found | Windows PowerShell < 6 / older OS | Use `sc.exe delete OllamaLitellmProxy` as an alternative |

---

## See Also

- [README](../README.md) — Full project overview and configuration reference
- [Using with Visual Studio 2026](visual-studio-2026-setup.md)
- [Using with VS Code](vscode-setup.md)
