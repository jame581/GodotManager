#!/usr/bin/env bash
# PostToolUse(Edit|Write): after an edit to Tui/TuiApp.cs, remind Claude that every
# TUI handler has a CLI counterpart that must go through the same elevation predicate.
# Advisory only — never blocks.
file=$(jq -r '.tool_input.file_path // empty')
case "$file" in
  "${CLAUDE_PROJECT_DIR:-$PWD}/GodotManager/Tui/TuiApp.cs") ;;
  *) exit 0 ;;
esac
jq -n '{hookSpecificOutput: {hookEventName: "PostToolUse", additionalContext:
  "TuiApp.cs changed. Every TUI handler here mirrors a CLI command in GodotManager/Commands/. Before finishing: check the matching *Command.cs goes through the same Elevated*.IsRequired predicate and handles ElevatedOperationResult (Error/Hint/Warning) the same way. Four past bugs came from the two front-ends drifting apart (see CLAUDE.md, \"Both front-ends must go through the same predicate\")."}}'
