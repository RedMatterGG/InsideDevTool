# MCP client examples

Example configurations for connecting MCP clients to the InsideDev MCP server in INSIDE. The game must be running with
`InsideDev.Mcp.dll` (and `InsideDev.Play.dll` for the play tools). Details: `docs/MCP.md` and `docs/PLAY.md`.

| Endpoint | Tools |
|---|---|
| `http://127.0.0.1:47811/mcp/play` | play profile, 14 tools: an AI that plays |
| `http://127.0.0.1:47811/mcp/dev` (also `/mcp`) | dev profile, 33 tools: inspecting and editing the world |
| `http://127.0.0.1:47811/mcp/all` | all 40 tools (the tool cap of Cursor) |

stdio, for clients that start a local program:

```
C:\path\to\InsideDevTool\tools\mcp\InsideDev.McpStdio.exe --profile play
C:\path\to\InsideDevTool\tools\mcp\InsideDev.McpStdio.exe --profile dev
```

Options: `--profile play|dev|all` (default dev), `--port <n>` (default 47811), `--url <endpoint>`, `--token <token>`
(or the environment variable `INSIDEDEV_MCP_TOKEN`). The program can be started before the game: until the game answers
it offers only `inside_status`.

| File | Client | Where it goes |
|---|---|---|
| `claude_code.md` | Claude Code | `claude mcp add` lines |
| `claude_code.mcp.json` | Claude Code | `.mcp.json` in the project folder |
| `claude_desktop_config.json` | Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` (stdio only) |
| `cursor.mcp.json` | Cursor | `%USERPROFILE%\.cursor\mcp.json` or `.cursor\mcp.json` in the project |
| `vscode.mcp.json` | VS Code (Copilot agent mode) | `.vscode\mcp.json` in the workspace |
| `codex.config.toml` | Codex CLI | merge into `%USERPROFILE%\.codex\config.toml` |
| `gemini.settings.json` | Gemini CLI | merge into `%USERPROFILE%\.gemini\settings.json` or `.gemini\settings.json` in the project |

Replace `C:\path\to\InsideDevTool` with the folder of the release (in JSON every `\` is written `\\`). Only Claude Code
was used with this server during development; the other files follow each client's documented format and are not tested
against this release.

Token: when `mcp.token` is set in `<game>\_mod\editor.cfg`, HTTP entries need the header
`Authorization: Bearer <token>` and stdio entries need `--token <token>` or `INSIDEDEV_MCP_TOKEN` in their environment.
Without a token (the default) nothing is needed.

Timeouts: one tool call can take up to about 57 s in the game (55 s budget plus grace) and up to 90 s through the stdio
program. Give the client a tool timeout of at least 90 s where it has one.

Images: clients that pass only `structuredContent` to the model drop the image of `observe {image}`; the `screenshot`
tool returns its image without structuredContent and works there.
