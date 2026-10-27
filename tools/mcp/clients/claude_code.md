# Claude Code

## HTTP (the game must be running when the client connects)

```
claude mcp add --transport http inside-play http://127.0.0.1:47811/mcp/play
claude mcp add --transport http inside-dev http://127.0.0.1:47811/mcp/dev
```

With a token (`mcp.token` in `<game>\_mod\editor.cfg`):

```
claude mcp add --transport http inside-play http://127.0.0.1:47811/mcp/play --header "Authorization: Bearer <token>"
```

## stdio (can be connected before the game starts)

```
claude mcp add inside-play -- "C:\path\to\InsideDevTool\tools\mcp\InsideDev.McpStdio.exe" --profile play
claude mcp add inside-dev -- "C:\path\to\InsideDevTool\tools\mcp\InsideDev.McpStdio.exe" --profile dev
```

With a token:

```
claude mcp add --env INSIDEDEV_MCP_TOKEN=<token> inside-play -- "C:\path\to\InsideDevTool\tools\mcp\InsideDev.McpStdio.exe" --profile play
```

Until the game answers, the stdio server lists only `inside_status`; the tool list updates when the game is up. Call
`inside_status` once after that: its result has the game's instructions.

## Scope

`claude mcp add` stores the server for the current project only (local scope). `-s user` adds it for every project,
`-s project` writes `.mcp.json` into the project folder (see `claude_code.mcp.json`). In `.mcp.json` a token can come
from the environment:

```json
"headers": { "Authorization": "Bearer ${INSIDEDEV_MCP_TOKEN}" }
```

## Check

`claude mcp list` shows the servers and whether they connect. In a session, `/mcp` shows the tools.
