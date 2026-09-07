# Rendering report

A fixture document. It exists so the artifact viewer can be *looked at* rather
than only unit-tested — every construct below is one the viewer styles, and
several of them were wrong at some point without any test noticing.

## What this exercises

- Headings down to level three, and the rule under the first two
- A table wide enough to need its own horizontal scrollbar
- A fenced code block with lines past the window edge
- `inline code`, a [link](https://github.com/SACRVM/firepit-ai), a blockquote
- A task list, because the checkbox colour is set explicitly
- A relative image, which only resolves through the viewer's virtual host

### Table

| Kind | Marker | Handled by | Why |
|---|---|---|---|
| Markdown | `.md` | Firepit's viewer | Windows has no association for it at all |
| Text | `.txt` `.log` | Firepit's viewer | Escaped, never rendered as markup |
| Image | `.png` `.jpg` | Firepit's viewer | Reached through the mapped virtual host, never `file://` |
| Document | `.pdf` `.docx` | The system handler | A real handler already exists and is better than a half-built one |
| Executable | `.exe` `.ps1` | The system handler | A click runs code, and the list says so before the click |

### Code

```csharp
// Long on purpose: the block has to scroll sideways instead of wrapping, and the
// scrollbar it grows has to sit on the block's own darker ground rather than frame it.
var html = ArtifactPreview.Build(ArtifactKind.Markdown, Path.GetFileName(absolutePath), File.ReadAllText(absolutePath));
```

### Quote

> Firepit is a transparent host. It does not parse, modify, or interpret PTY
> output beyond timestamping reads for activity detection.

### Task list

- [x] Render markdown without shelling out to a browser
- [x] Escape plain text instead of rendering it
- [ ] Anything that needs scripting — deliberately never

### Image

![A fixture diagram](diagram.png)

---

A paragraph with no line breaks in it at all, long enough that it has to wrap several times over even in a wide window, which is the case that showed the document was capped at sixty rem but never centred, so a maximised viewer put the text down the left and left a dead strip down the right.
