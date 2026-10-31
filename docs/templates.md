# Prompt templates

Without an API key, **Update with Claude** and **Update all** show a prompt you can paste into Claude Code or claude.ai. Edit both templates in Settings → Prompt template.

## One note

| Placeholder | Becomes |
|---|---|
| `{project}` | Project name |
| `{project_root}` | Project folder |
| `{note}` | Note title |
| `{note_path}` | Full path to the note |
| `{vault}` | Vault folder |
| `{changed_files}` | One line per changed file: full path, status and changed lines |
| `{deleted_files}` | Deleted files, or `none` |
| `{functions_added}` | New functions, or `none` |
| `{functions_removed}` | Removed functions, or `none` |
| `{note_text}` | The note's full text |
| `{code}` | Full contents of the changed files |

`{note_text}` and `{code}` paste in file contents. Use them only for chats that can't open files.

## Update all

| Placeholder | Becomes |
|---|---|
| `{notes}` | One section per flagged note: path, changed files with lines, functions |
| `{count}` | Number of notes |
| `{vault}` | Vault folder |

Placeholders the app doesn't know are left as written. **Reset** restores the default.
