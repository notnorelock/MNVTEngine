@AGENTS.md

## Claude Code

- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- For edits containing backslashes (Windows paths, C# escapes, char literals), use the Edit tool, not Python or heredoc scripts.
  Python on this machine defaults to cp1250: run scripts with `python -X utf8`.
