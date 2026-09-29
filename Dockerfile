# McpMemoryService Dockerfile (M11)
# Multi-stage build: SDK build → ASP.NET Core runtime

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj + restore (layer cache)
COPY src/McpMemoryService/McpMemoryService.csproj ./McpMemoryService/
RUN dotnet restore ./McpMemoryService/McpMemoryService.csproj

# Copy source + build
COPY src/McpMemoryService/ ./McpMemoryService/
RUN dotnet publish ./McpMemoryService/McpMemoryService.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install no CUDA/libcuda dependencies (per SPEC §6.1)
# aspnet:10.0 base image has no CUDA — no action needed

# Copy published output
COPY --from=build /app/publish ./

# ONNX model + tokenizer are NOT baked into the image (deployment decision).
# The model lives on the host (TrueNAS dataset) and is mounted at /app/Models
# at runtime via docker-compose volume or docker run -v.
# See: deployment decision (Var B - model stored on TrueNAS dataset).
# Ensure the directory exists so the volume mount target is valid.
RUN mkdir -p /app/Models

# Expose MCP port
EXPOSE 5000

# Environment defaults (overridable via docker-compose)
ENV ASPNETCORE_URLS=http://+:5000
ENV MODEL_DIR=/app/Models

# Install curl for healthcheck (required by docker-compose healthcheck)
# BUG_FIX_CONTEXT: M11 QA BLOCK (AC-4) — aspnet:10.0 base image does NOT bundle curl.
# Confirmed from dotnet/dotnet-docker: src/runtime-deps/10.0/{noble,resolute}/amd64/Dockerfile
# install only ca-certificates, libc6, libgcc-s1, libicu, libssl3t64, libstdc++6, tzdata.
# The compose healthcheck ["CMD","curl","-f","http://localhost:5000/health"] requires curl.
# Fix: apt-get install curl with --no-install-recommends + cleanup to minimize image bloat.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Non-root user (security best practice)
# BUG_FIX_CONTEXT: M11 QA BLOCK (AC-6) — aspnet:10.0 resolves to Ubuntu noble 24.04,
# which ships useradd but omits the adduser Perl wrapper. The plan Notes #4 premise
# ("Debian-based" = bundles adduser) is stale for .NET 10. Base-image probe confirmed:
# `docker run --rm mcr.microsoft.com/dotnet/aspnet:10.0 bash -lc "command -v adduser"` → empty.
# Fix: use useradd directly (present in all Linux base images).
# BUG_FIX_CONTEXT: [HYPOTHESIS: useradd --uid 1000 hard-errors with exit code 4 because
# UID 1000 is pre-allocated to the `ubuntu` user in the aspnet:10.0 noble base image.
# useradd refuses to reuse an existing UID even when the username differs.]
# Verified 2026-07-02 via `docker run --rm aspnet:10.0 getent passwd ubuntu` →
# `ubuntu:x:1000:1000:Ubuntu:/home/ubuntu:/bin/bash` (UID 1000 already taken).
# Fix: drop --uid 1000 — let useradd auto-select next free UID, matching original
# adduser semantics (which would auto-assign the next free UID). USER appuser stays
# valid because it references the username, not the UID.
RUN useradd --create-home --shell /usr/sbin/nologin appuser
USER appuser

ENTRYPOINT ["dotnet", "McpMemoryService.dll"]