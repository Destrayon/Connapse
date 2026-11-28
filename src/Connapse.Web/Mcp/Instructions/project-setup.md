# Set up Connapse for this project

Follow these steps in order, end to end, in the project the user has open. Ask the user exactly two questions, both in step 2: which saved login to use, and whether agents reach Connapse through the CLI or MCP. Decide everything else yourself from what the project and Connapse show you. Each step says what to check first, so a project that is partly set up keeps what it has. When you finish, tell the user what you set up and where you recorded it.

Connapse tools are named below without the prefix your client adds (Claude Code calls `memory_recall` `mcp__connapse__memory_recall` when the server is named `connapse`). CLI commands are for the `connapse` command-line tool.

## 1. Gather what you need for the questions

1. Read the project's `CLAUDE.md` (and any file it points to for Connapse), `.claude/settings.json`, `.claude/settings.local.json` and `.mcp.json` if they exist. Note any Connapse login (profile), organization, access route, memory space and containers they already name. Reuse what they name in every step below instead of making something new, unless it no longer exists.
2. Run `connapse --version`. If the CLI isn't installed, install it as https://github.com/Destrayon/connapse-cli#install describes. Logins are kept by the CLI even when agents use MCP.
3. Run `connapse auth list` to see the saved logins. A login is a saved CLI profile: a server, a sign-in (account), and a default organization. Several logins can share a server and differ only by account or organization, and `connapse auth list` doesn't show the organization, so for each login run `connapse auth whoami --profile <name>` (its server and account) and `connapse org current --profile <name>` (its default organization; on a self-hosted server it says organizations don't apply). On Connapse Cloud, also run `connapse org list --profile <name>` to see the other organizations that account belongs to.

## 2. Ask the user the two questions

Ask both at once, with your recommendation first in each.

1. **Which login?** List each saved login with its server, account and organization, and add the option "Sign in a new login" (whoever picks it gives the server URL and, on Connapse Cloud, the organization). When the project needs an organization that no login has as its default but an account belongs to (its files name one, or the user says so), also offer that login with that organization. Recommend the login step 1 found in the project's files, else the one whose server and organization already hold containers or memory spaces named after this project.
2. **CLI or MCP?** Say in one line each: with the **CLI**, agents run `connapse ...` commands in the shell, and the login is picked per project with `CONNAPSE_PROFILE`; with **MCP**, agents call Connapse's tools directly through an MCP server connected to the login's server. Recommend the route the project's files already record, else the one this session is using now.

## 3. Set the login for the project

1. If the user chose a new login, pick a short profile name from the server's host name (such as `home` for `connapse.home.lan`) and sign it in: `connapse auth login --profile <name> --server <url>`. It opens a browser; tell the user to finish signing in there, and wait for the command to return. Check it with `connapse auth whoami --profile <name>`.
2. Organizations exist only on Connapse Cloud; on a self-hosted server the project has no organization. On Cloud, use the organization the user picked with the login, else the one the project's files name, else the login's current default (`connapse org current --profile <name>`). For a new login, pick it with `connapse org switch <slug> --profile <name>`.
3. Set the login in `.claude/settings.json` (create it if missing; merge, never replace other keys):

   ```json
   {
     "env": {
       "CONNAPSE_PROFILE": "<profile name>",
       "CONNAPSE_ORG": "<organization slug, only on Connapse Cloud>"
     }
   }
   ```

   Leave `CONNAPSE_ORG` out when there is no organization. On Cloud, always set it: it is how a project uses an organization other than the login's default, and it keeps the project on its organization when that default changes later. Claude Code passes `env` to every command it runs, so each `connapse` command in this project uses this login. The setting takes effect in the next session; until then, pass `--profile <name>` (and `--org <slug>`) on each command you run.

If the user chose **MCP**, make sure the project's MCP server talks to the login's server:

4. A URL alone doesn't say who an MCP connection is signed in as: `CONNAPSE_PROFILE` picks the CLI's login, but MCP tools act as whoever authenticated the connection, and several logins can share a server. If this session has a Connapse MCP server whose URL is the login's server, call `connapse_api_read` with `GET /api/v1/auth/me` and compare its email with the login's account from step 1; on Connapse Cloud, also check the connection is in the chosen organization. Keep the server only when both match.
5. If they don't match, tell the user to run `/mcp` in Claude Code, choose **Authenticate** (or clear it and authenticate again) on that server and sign in with the login's account. If there is no such server, add one for this project: `claude mcp add --transport http --scope project connapse <server url>/mcp`. That writes `.mcp.json`, which everyone working in the repository shares; then have the user authenticate it the same way. New or re-signed tools take effect in the next session.
6. Until the MCP connection is confirmed to be the chosen login, don't use Connapse's MCP tools for the steps below: use the CLI with `--profile <name>` (and `--org <slug>`), so nothing is read or created as the wrong account.

If the user chose **CLI**:

7. Run `connapse setup claude-code --profile <name>`. It prints Claude Code auto mode settings that tell auto mode the signed-in server is the user's own service, so it stops denying routine Connapse commands. Claude Code reads `autoMode` only from `~/.claude/settings.json`, so merge the printed block there, keeping every other key, and tell the user you did.

## 4. Pick the memory space

A memory space holds the project's memories: dated facts that agents save and recall. A personal space is seen only by its owner; a team space by everyone on the server (or organization). The memories built-in memory would keep (the user's preferences, their feedback, what they're working on) are private, so they always go to a **personal** space. Never make a space team on your own: sharing is the user's decision, and a repository having a remote doesn't mean they made it.

1. If step 1 found a memory space, check that it still exists and note its kind: `memory_spaces` with `name`, or `connapse api GET "/api/memory/spaces?q=<name>"`. Use it if it is personal. If it is a team space, keep it as the project's **shared** space, for facts meant for everyone on the project, and still pick a personal space below.
2. Otherwise list the spaces (`memory_spaces` with `kind` "personal", or `connapse api GET "/api/memory/spaces?kind=personal"`) and reuse a personal one whose name or description says it is this project's.
3. Otherwise create a personal one named after the project, such as `<project>-memory`, with a description saying it holds memories for this project: `memory_space_create` with `kind` "personal", or `connapse api POST /api/memory/spaces --data '{"name":"<name>","kind":"personal","description":"<what it holds>"}'`.

Memories are saved with `memory_remember` (or `connapse memory save <subject> <attribute> <value> --space <name>`) and recalled with `memory_recall` (or `connapse memory recall <query> --space <name>`).

## 5. Pick the containers for sources

Containers keep unaltered sources: documents as they came (specs, papers, transcripts, exports, reference pages), uploaded whole and never rewritten. Memories cite them; containers are not the place for notes or summaries.

1. Start from the containers step 1 found. Check each still exists with `container_list` (or `connapse container list`).
2. Decide which kinds of source this project will keep, from `CLAUDE.md`, the README and the repository's contents.
3. For each kind, reuse a container that `CLAUDE.md` names or whose name and description fit it.
4. Only when none fits, create one: `container_create` (or `connapse container create <name> --description "<what it holds>"`), named `<project>-<topic>`, with a description saying what goes in it. Make as few as the project needs; one is often enough.

Upload sources with `upload_file` (or `connapse upload <path> --container <name>`).

## 6. Record the setup

Write down what steps 2 to 5 chose so every later session finds it without asking. Record:

- The access route (CLI or MCP), the login (profile) and its server, and the organization if there is one.
- The personal memory space (and the team space, if step 4 kept one, with what belongs there), with one line each on how to recall and save through this project's access route.
- Each container with one line on what goes in it.

Put this in a `## Connapse` section of `CLAUDE.md`, replacing any older one. Claude Code loads `CLAUDE.md` into every session, so keep there only what every session needs. If the section would run past about 15 lines (usually because of many containers), keep the login, the memory space and how to use it in `CLAUDE.md`, move the container list to `docs/connapse.md`, and add one line to the section saying to read `docs/connapse.md` before uploading or searching sources.

## 7. Block Claude Code's built-in memory

Claude Code has its own memory: a `memory` folder inside this project's folder under `~/.claude/projects/`, with a `MEMORY.md` index. Turn it off for this project so memories go only to the memory space.

1. In `.claude/settings.json`, set `"autoMemoryEnabled": false`.
2. Find a Python that runs (`python3 --version`, else `python --version`). If neither does, write the script below in Node.js instead, with the same behavior.
3. Write this script to `.claude/hooks/connapse_memory.py`, filling in the values at the top (`SHARED` stays empty unless step 4 kept a team space):

   ```python
   """Keep this project's memory in Connapse, not Claude Code's built-in memory.

   session -> SessionStart: tell the agent where memory lives.
   pretool -> PreToolUse: deny any tool call that touches the built-in memory.
   """
   import json
   import re
   import sys

   SPACE = "<personal memory space name>"
   SHARED = ""  # "<team memory space name>" only if the project already uses one
   ROUTE = "<cli or mcp>"
   TOOLS = "<the prefix your client gives Connapse's tools, such as mcp__connapse__>"

   BUILT_IN_MEMORY = re.compile(
       r"\.claude/projects/[^/\s\"']+/memory(/|\b)|\.claude/[^\s\"']*MEMORY\.md", re.IGNORECASE)

   if ROUTE == "cli":
       RECALL = f'`connapse memory recall "<query>" --space {SPACE}`'
       SAVE = f"`connapse memory save <subject> <attribute> <value> --space {SPACE}`"
   else:
       RECALL = f"`{TOOLS}memory_recall` with space=\"{SPACE}\""
       SAVE = f"`{TOOLS}memory_remember` with space=\"{SPACE}\""


   def session():
       context = (
           "# Memory: Connapse only\n\n"
           "Claude Code's built-in memory is off in this project: never read or write the memory "
           "folders under ~/.claude/projects/ or their MEMORY.md index; a hook blocks those calls.\n"
           f"This project's memories live in the personal Connapse memory space `{SPACE}`, which only its owner sees.\n"
           f"- Recall: before relying on remembered context, run {RECALL}.\n"
           f"- Save: run {SAVE}. Save what built-in memory would (the user's preferences, "
           "feedback with its reason, non-obvious project context), not what the repo already records.\n"
           + (f"- The team space `{SHARED}` is seen by everyone on the project. Save there only facts the "
              "user wants shared; never their preferences or private feedback.\n" if SHARED else "") +
           "- If Connapse can't be reached, tell the user memory is unavailable; never fall back to local files.\n"
           "CLAUDE.md's Connapse section names the login and the containers for sources."
       )
       print(json.dumps({"hookSpecificOutput": {
           "hookEventName": "SessionStart", "additionalContext": context}}))


   def strings_in(value):
       if isinstance(value, str):
           yield value
       elif isinstance(value, dict):
           for v in value.values():
               yield from strings_in(v)
       elif isinstance(value, list):
           for v in value:
               yield from strings_in(v)


   def pretool(raw):
       try:
           texts = list(strings_in(json.loads(raw).get("tool_input") or {}))
       except (ValueError, AttributeError):
           texts = [raw]  # unreadable input is scanned whole, so it can't slip past
       if any(BUILT_IN_MEMORY.search(t.replace("\\", "/")) for t in texts):
           print(json.dumps({"hookSpecificOutput": {
               "hookEventName": "PreToolUse",
               "permissionDecision": "deny",
               "permissionDecisionReason": "Built-in memory is off in this project. "
                   f"Use the Connapse memory space `{SPACE}`: recall with {RECALL}, save with {SAVE}.",
           }}))


   if __name__ == "__main__":
       raw = sys.stdin.read()
       mode = sys.argv[1] if len(sys.argv) > 1 else ""
       if mode == "session":
           session()
       elif mode == "pretool":
           pretool(raw)
   ```

4. Register both hooks in `.claude/settings.json`, merged with any hooks already there, using the Python command from step 2:

   ```json
   {
     "hooks": {
       "SessionStart": [
         { "hooks": [ { "type": "command", "command": "python3 \"$CLAUDE_PROJECT_DIR/.claude/hooks/connapse_memory.py\" session", "timeout": 10 } ] }
       ],
       "PreToolUse": [
         { "matcher": "Read|Write|Edit|MultiEdit|NotebookEdit|Glob|Grep|Bash|PowerShell",
           "hooks": [ { "type": "command", "command": "python3 \"$CLAUDE_PROJECT_DIR/.claude/hooks/connapse_memory.py\" pretool", "timeout": 10 } ] }
       ]
     }
   }
   ```

5. Test both: pipe `{}` into the script with `session` and check it prints the memory notice, then pipe a `tool_input` whose `file_path` is a file in one of those memory folders into it with `pretool`, and check it prints a `deny`.

## 8. Finish

Check that `.claude/settings.json` is valid JSON. Tell the user, in a few lines: the access route, the login and organization, the memory space (and whether you made it), the containers (and which you made), where you recorded them, and that the settings, MCP server and hooks take effect in the next Claude Code session. Don't commit the changes unless the user asks.
