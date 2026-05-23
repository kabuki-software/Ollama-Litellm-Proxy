# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is an ASP.NET Core reverse proxy project that mimics the Ollama API while redirecting requests to a [LiteLLM](https://github.com/BerriAI/litellm) backend. It uses YARP (Yet Another Reverse Proxy) to handle request forwarding and includes custom transforms to adapt the Ollama API schema to the LiteLLM/OpenAI-compatible schema.

## Build and Run Commands

- **Build and run**: `dotnet run`
- **Build**: `dotnet build`
- **Run in development**: `dotnet run --launch-profile "Development"`

The application listens on `http://localhost:11434` by default and forwards requests to `http://192.168.0.106:4000` (configurable in `appsettings.json`).

## Windows Service

A PowerShell script is provided to install or uninstall the application as a Windows service:

```powershell
# Install (run as Administrator)
.\service.ps1 -Install

# Uninstall (run as Administrator)
.\service.ps1 -Uninstall
```

The service is named `OllamaLitellmProxy` by default and is set to start automatically.

## Architecture

### Core Components

- **Program.cs**: Main application entry point, configures YARP reverse proxy, synthetic Ollama endpoints, and logging middleware
- **StandardTransform.cs**: Custom YARP transform provider that handles API endpoint mapping, request body patching, and response transformation
- **ModelData.cs**: Data models for JSON serialization/deserialization between Ollama and LiteLLM/OpenAI schemas

### Key Features

1. **API Translation**: Converts between Ollama and LiteLLM/OpenAI-compatible schemas
2. **Endpoint Mapping**:
   - `/api/tags` → `/models`
   - `/v1/chat/completions` → `/chat/completions`
   - `/api/show` → returns synthetic model information (handled locally, not forwarded)
   - `/api/version` → returns static version info (handled locally, not forwarded)
3. **Request Patching**: Injects `reasoning_content` on assistant messages for DeepSeek thinking-mode models to prevent HTTP 400 errors from clients unaware of this field
4. **Authorization Header Injection**: Adds a `Bearer` token to all forwarded requests via YARP transform configuration
5. **Response Transformation**: Transforms `/models` responses from the LiteLLM schema to the Ollama schema
6. **Request Logging**: Logs all incoming proxy requests and proxy errors

### Configuration

- **appsettings.json**: Contains YARP configuration, logging levels, and Kestrel settings
- **ReverseProxy section**: Defines routes, clusters, and request header transforms (e.g., Authorization)
- **Kestrel section**: Configures server limits (large body size, extended timeouts) and the listening endpoint

### Data Flow

1. Client makes request to proxy (port 11434)
2. Synthetic endpoints (`/api/version`, `/api/show`) are handled locally and never forwarded
3. `StandardTransform` rewrites the request path and patches the request body as needed
4. YARP forwards the request to the LiteLLM backend (default: `http://192.168.0.106:4000`)
5. `StandardTransform` transforms the response body (e.g., `/models` schema conversion)
6. Response is returned to the client

## Dependencies

- **.NET 9.0**: Target framework
- **Yarp.ReverseProxy 2.3.0**: Core reverse proxy functionality
- **Newtonsoft.Json 13.0.3**: JSON serialization/deserialization