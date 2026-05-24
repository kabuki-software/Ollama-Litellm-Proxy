# Ollama-LiteLLM-Proxy

This project is an ASP.NET Core reverse proxy that exposes the same endpoints as Ollama and transparently forwards requests to a [LiteLLM](https://github.com/BerriAI/litellm) backend (default: `http://192.168.0.106:4000`). It uses YARP (Yet Another Reverse Proxy) and custom transforms to rewrite paths, patch request bodies, and adapt response schemas for full Ollama API compatibility.


## History

At the end of May 2026, Microsoft changed their usage policy for Github Copilot. As a fan of Github Copilot and a daily user of Microsoft Visual Studio 2026 I've been hindered by the current inability of Copilot in VS 2026 to be able to use alternative LLM models without having to host them locally. This project addresses that limitation by using the version of Copilot in VS 2026's ability to access locally hosted models using Ollama, and extends the options available to the developer who can now add 100+ hosted LLM models to Visual Sutdio 2026 using an instance of LiteLLM Proxy Server (AI Gateway). Ollama-LiteLLM-Proxy acts as Ollama and exposes the same endpoints as Ollama and transparently forwards them to LiteLLM.

Credit to  [OllamaYarpProxy](https://github.com/alkampfergit/OllamaYarpProxy).

## Features

- **Ollama API Compatibility:** Exposes `/api/tags`, `/api/show`, `/api/version`, and `/v1/chat/completions`, rewriting and transforming requests/responses as needed.
- **LiteLLM Backend:** Forwards requests to a LiteLLM server; the target URL is configurable via `appsettings.json`.
- **Authorization Header Injection:** Automatically adds a `Bearer` token to all forwarded requests.
- **Thinking-Mode Compatibility:** Injects the `reasoning_content` field on all assistant messages before forwarding. This is required by DeepSeek thinking-mode models (omitting it causes HTTP 400), and is safely ignored by all other OpenAI-compatible backends.
- **Response Transformation:** Converts `/models` responses from the LiteLLM/OpenAI schema to the Ollama schema.
- **Logging:** Logs incoming requests, proxy destinations, and errors for easier debugging.

## How to Run

1. **Build and run the project:**
   ```pwsh
   dotnet run
   ```
2. **Access the proxy:**  
   By default, it listens on `http://localhost:11434` and forwards requests to `http://192.168.0.106:4000`.

## Windows Service

The project includes a PowerShell script to install or uninstall it as a Windows service (set to start automatically):

```powershell
# Install (run as Administrator)
.\service.ps1 -Install

# Uninstall (run as Administrator)
.\service.ps1 -Uninstall
```

The script is automatically copied to the build output directory.

## Configuration

- **Backend URL:** Change the target in `appsettings.json` under `ReverseProxy → Clusters → ollamaCluster → Destinations`.
- **Authorization Token:** Update the `Bearer` token in the `Transforms` section of the `ollama` route in `appsettings.json`.
- **Logging:** Controlled via `appsettings.json` or environment variables.

## Endpoints

| Incoming (Ollama) | Forwarded (LiteLLM) | Notes |
|---|---|---|
| `GET /api/tags` | `GET /models` | Response schema adapted to Ollama format |
| `POST /v1/chat/completions` | `POST /chat/completions` | `reasoning_content` injected on all assistant messages (required by DeepSeek, ignored by others) |
| `POST /api/show` | *(handled locally)* | Returns synthetic model info in Ollama format |
| `GET /api/version` | *(handled locally)* | Returns static Ollama-compatible version |
| All other paths | forwarded as-is | |

---

## See Also

- [Using with Visual Studio 2026](src/docs/visual-studio-2026-setup.md)
- [Using with VS Code](src/docs/vscode-setup.md)
- [Installing as a Windows Service](src/docs/windows-service-setup.md)
- [CLAUDE.md](src/docs/claude.md) — Guide for working with this codebase in Claude Code
