# Using Ollama-LiteLLM-Proxy with VS Code

VS Code with the [GitHub Copilot extension](https://marketplace.visualstudio.com/items?itemName=GitHub.copilot) supports using a local Ollama-compatible endpoint as the AI backend. This proxy lets you transparently route those requests to a [LiteLLM](https://github.com/BerriAI/litellm) server.

> [!IMPORTANT]
> **The proxy must run on the same machine as VS Code.**
> VS Code's Copilot integration only accepts Ollama endpoint URLs that resolve to `localhost`. Pointing it at a remote host — even on the same LAN — will silently fail or be rejected. Run this proxy locally; it will forward requests to your LiteLLM server wherever that lives.

---

## Prerequisites

- [VS Code](https://code.visualstudio.com/) with the [GitHub Copilot](https://marketplace.visualstudio.com/items?itemName=GitHub.copilot) extension installed and signed in
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- A running [LiteLLM](https://github.com/BerriAI/litellm) server accessible on your network
- This proxy built and running (see [README](../README.md))

---

## Step 1 — Configure the Proxy

Open `appsettings.json` and set your LiteLLM backend address and API key:

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

## Step 3 — Point VS Code Copilot to the Proxy

VS Code exposes Copilot's Ollama endpoint through the user or workspace settings.

1. Open **Settings** (`Ctrl+,`).
2. Search for **`github.copilot.advanced`** or navigate to **Extensions → GitHub Copilot**.
3. Click **Edit in settings.json** and add (or update) the following:

```jsonc
{
  // Tell Copilot to use a local Ollama-compatible endpoint
  "github.copilot.advanced": {
	"ollamaBaseUrl": "http://localhost:11434"
  }
}
```

4. Save the file. VS Code will pick up the change immediately — no restart required.

> **Tip:** To see which models are available through the proxy, run:
> ```powershell
> Invoke-RestMethod http://localhost:11434/api/tags | ConvertTo-Json -Depth 5
> ```
> Use one of the returned model names when VS Code asks you to select a model.

---

## Step 4 — Verify the Connection

In a terminal, confirm the proxy is reachable and returning a model list:

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
| Copilot shows "No Ollama models found" | Proxy not running | Start the proxy or the Windows service |
| Empty model list in VS Code | LiteLLM `/models` returns no models | Check LiteLLM configuration and available providers |
| HTTP 401 from LiteLLM | Wrong API key | Update `Bearer` token in `appsettings.json` |
| HTTP 400 on chat completions with DeepSeek | Missing `reasoning_content` | Handled automatically — the proxy injects it on all requests |
| Port 11434 already in use | Another Ollama instance is running | Stop the local Ollama service or change the proxy port (see below) |
| Setting has no effect | Wrong settings key | Verify the key is `github.copilot.advanced.ollamaBaseUrl` in your `settings.json` |

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

Then update your VS Code `settings.json` to match:

```jsonc
{
  "github.copilot.advanced": {
	"ollamaBaseUrl": "http://localhost:12434"
  }
}
```

---

## See Also

- [Visual Studio 2026 setup](visual-studio-2026-setup.md)
- [Installing as a Windows Service](windows-service-setup.md)
- [README](../README.md) — Full project overview and configuration reference
- [LiteLLM documentation](https://docs.litellm.ai/)
- [GitHub Copilot extension](https://marketplace.visualstudio.com/items?itemName=GitHub.copilot)
