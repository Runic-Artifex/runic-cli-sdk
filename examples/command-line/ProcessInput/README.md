# Process input tool chain

This self-contained example runs its own executable as three allowlisted tools.
It sends a copied UTF-8 payload to `normalize`, supplies that tool's bounded
output to `count`, and closes stdin immediately for `health`. Each tool starts
with an isolated environment containing only explicit application/runtime values.

From the repository development shell:

```bash
dotnet run --project examples/command-line/ProcessInput -c Release -- stone and gold
```

Expected output:

```text
Normalized: STONE AND GOLD
Word count: 3
Health: ready
```

Each input and retained stdout is capped at 16 KiB, stderr at 4 KiB, and each
execution at ten seconds. Ctrl+C cancels the active child. The chain verifies
successful exit and complete retained output before feeding the next tool.
`DOTNET_ROOT` variants are explicitly copied when present so framework-dependent
apphosts work in custom .NET installations; other parent variables are omitted.
Use absolute paths and your own executable policy when replacing these sample
tools with application tools.
