# Using Ollama-LiteLLM-Proxy with Visual Studio 2026

Visual Studio 2026 has built-in AI code completion powered by GitHub Copilot. By pointing it at a local Ollama-compatible endpoint you can route those requests through your own LiteLLM backend instead of the default cloud service. This guide shows you how.

> [!IMPORTANT]
> **The proxy must run on the same machine as Visual Studio 2026.**
> As of this writing, Visual Studio 2026 only accepts Ollama endpoint URLs that resolve to `localhost` (i.e. `http://localhost:<port>`). Pointing it at a remote host — even one on the same LAN — does not work and the connection will silently fail or be rejected. Install and run this proxy locally on your development machine, even if your LiteLLM backend lives on a different server.

---

## Prerequisites

- Visual Studio 2026 installed (any edition)
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- A running [LiteLLM](https://github.com/BerriAI/litellm) server accessible on your network
- This proxy built and running (see [README](../README.md))

---

## Step 1 — Configure the Proxy

Open `appsettings.json` and set the correct LiteLLM backend address and API key:

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
```

Update the `Bearer` token to match your LiteLLM master key:

```json
"Transforms": [
  { "RequestHeader": "Authorization", "Set": "Bearer <your-litellm-key>" }
]
```

---

## Step 2 — Start the Proxy

### Option A — Run directly

```powershell
dotnet run --project src/Ollama-Litellm-Proxy
```

or from the project folder:

```powershell
dotnet run
```

The proxy listens on `http://localhost:11434` by default.

### Option B — Install as a Windows Service

Run once as Administrator so the proxy starts automatically with Windows:

```powershell
.\service.ps1 -Install
```

To stop and remove it later:

```powershell
.\service.ps1 -Uninstall
```

---

## Step 3 — Point Visual Studio 2026 to the Proxy

Visual Studio 2026 allows you to configure a custom Ollama endpoint for AI features:

1. Open **Tools → Options**.
2. Navigate to **GitHub Copilot → (General)** (or **AI Assistant → Models**, depending on your VS version).
3. Under the **Local model / Ollama** section, set:
   - **Endpoint URL:** `http://localhost:11434`
4. Click **OK** to save.

> **Tip:** If Visual Studio asks for a model name, use any value returned by the proxy's `/api/tags` endpoint. You can check available models in a terminal:
> ```powershell
> Invoke-RestMethod http://localhost:11434/api/tags | ConvertTo-Json -Depth 5
> ```

---

## Step 4 — Verify the Connection

In a PowerShell terminal, confirm the proxy is reachable and returns a model list:

```powershell
# Should return a JSON object with a "models" array
Invoke-RestMethod http://localhost:11434/api/tags

# Should return { "version": "0.9.6" }
Invoke-RestMethod http://localhost:11434/api/version
```

If either call fails, check that:
- The proxy process is running (or the Windows service is started)
- No firewall rule blocks port `11434` on localhost
- The LiteLLM backend is reachable at the address configured in `appsettings.json`

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| VS shows "Cannot connect to Ollama" | Proxy not running | Start the proxy or the Windows service |
| Empty model list in VS | LiteLLM `/models` returns no models | Check LiteLLM configuration and available providers |
| HTTP 401 from LiteLLM | Wrong API key | Update `Bearer` token in `appsettings.json` |
| HTTP 400 on chat completions with DeepSeek | Missing `reasoning_content` | Handled automatically — the proxy injects it on all requests |
| Port 11434 already in use | Another Ollama instance is running | Stop the local Ollama service or change the proxy port in `appsettings.json` under `Kestrel → Endpoints → Http → Url` |

---

## Changing the Proxy Port

If port `11434` conflicts with a local Ollama installation, change the listening port in `appsettings.json`:

```json
"Kestrel": {
  "Endpoints": {
	"Http": {
	  "Url": "http://localhost:12434"
	}
  }
}
```

Then update the endpoint URL in Visual Studio Options to match (e.g. `http://localhost:12434`).

---

## See Also

- [VS Code setup](vscode-setup.md)
- [Installing as a Windows Service](windows-service-setup.md)
- [README](../README.md) — Full project overview and configuration reference
- [LiteLLM documentation](https://docs.litellm.ai/)
- [YARP documentation](https://microsoft.github.io/reverse-proxy/)
