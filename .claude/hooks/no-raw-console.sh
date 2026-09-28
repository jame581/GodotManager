#!/usr/bin/env bash
# PostToolUse(Edit|Write): all user-facing output goes through Spectre.Console
# (AnsiConsole.*). Flag raw Console.Write*/Console.Error.Write* in app code.
file=$(jq -r '.tool_input.file_path // empty')
case "$file" in
  "${CLAUDE_PROJECT_DIR:-$PWD}"/GodotManager/*.cs) ;;
  *) exit 0 ;;
esac
[ -f "$file" ] || exit 0
hits=$(grep -nE '\bConsole\.(Error\.)?Write(Line)?\b' "$file") || exit 0
{
  echo "Raw Console output in $file — this repo routes all user-facing output through Spectre.Console (AnsiConsole.*), see CLAUDE.md Conventions:"
  echo "$hits"
} >&2
exit 2
