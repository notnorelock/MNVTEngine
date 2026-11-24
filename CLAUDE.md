@AGENTS.md

## Claude Code

- Commit finished work without being asked, grouped into logical commits (one per change, unrelated user edits apart).
  No AI attribution anywhere: no `Co-Authored-By` trailer, no mention of Claude or an assistant in messages or PRs.
  Push only when asked.
- For edits containing backslashes (Windows paths, C# escapes, char literals), use the Edit tool, not Python or heredoc scripts.
  Python on this machine defaults to cp1250: run scripts with `python -X utf8`.
