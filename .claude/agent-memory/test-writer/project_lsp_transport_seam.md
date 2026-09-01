---
name: project_lsp_transport_seam
description: The LSP client's transport/protocol seam designed test-first in Sept 2026 — what it looks like and which contracts the tests pinned
metadata:
  type: project
---

`docs/plans/lsp.md` plans an LSP client for the Files pane (hover, diagnostics, go to definition). The
transport and protocol layer — the plan's top two rows — was designed test-first as a standalone
`GitBench.Lsp` library with 149 xunit tests. The prototype lived in a scratchpad and is gone; the
design decisions are the part worth keeping.

**Why:** the plan's own risk list puts "wrong positions" first and "servers behave differently from
each other" third, so the seam was shaped to make both unrepresentable rather than merely tested.

**How to apply** — when this layer is actually built, these are the shapes the tests demanded:

- `LspResponse<T>` is one closed union: `Ok | Retryable | Failed | Malformed | TimedOut | Cancelled |
  Disconnected`. Nothing throws for a protocol or transport outcome, cancellation included — an
  exception would put one case outside the caller's switch. `Retryable` exists because a server that
  is still indexing answers `ContentModified` / `ServerNotInitialized`, and treating that as failure
  makes rust-analyzer look broken for its first 30 seconds.
- Hover's three wire shapes and definition's three collapse at the boundary into `Hover.None |
  Hover.Text(kind, value, range)` and `Definition.None | Definition.Targets`. Plain text must not be
  promoted to markdown, and a `LocationLink` jumps to `targetSelectionRange`, not `targetRange`.
- Framing is its own unit (`LspFrameReader`/`LspFrameWriter`) so hostile input is tested without async
  plumbing. Decisions the tests pinned: strict CRLF, `Content-Length` authoritative, non-header lines
  skipped as server chatter (with the discarded byte count reported), payload cap checked before
  buffering, and clean close / truncation / malformed as three distinct outcomes.
- Every id gets a type: `RequestId` is a sum (number **or** string — a server asking with `"cfg-1"`
  must be answered with `"cfg-1"`), plus `DocumentUri`, `DocumentVersion`, `LineNumber`,
  `CharacterOffset`, `LspMethod`, `LspErrorCode`.
- Time is `TimeProvider` with a hand-written fake — no `Microsoft.Extensions.TimeProvider.Testing`
  package needed, and the whole suite runs in ~70 ms with no sleeps.

See also [[feedback_deterministic_concurrency_fakes]].
