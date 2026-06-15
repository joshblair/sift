---
title: "Adding Markdown Support End-to-End (Part 7)"
published: false
description: "What it actually takes to wire a new file type through a layered RAG stack — validation, extraction, MIME quirks, and a schema migration."
tags: aws, serverless, python, architecture
series: "Building Sift: A Multi-Tenant AI Platform on AWS"
---

# Adding Markdown Support End-to-End (Part 7)

*What it actually takes to wire a new file type through a layered RAG stack — validation, extraction, MIME quirks, and a schema migration.*

---

One of the things I deliberately built into Sift is a clear separation of concerns across the stack. The API validates what's allowed, the pipeline handles extraction, the frontend controls what you can drop, and the database enforces the constraint at rest. That structure pays off when you add features — but it also means "add Markdown support" touches more layers than you might expect.

This post walks through what that change actually looked like: every file it touched, the subtle bug that emerged in the browser, and the schema migration that closed the loop.

---

## Where File Types Are Enforced

Before making any change, I mapped out every place the stack has an opinion about file types:

1. **C# API** — `DocumentsFunction` validates the extension before issuing a presigned upload URL
2. **C# service layer** — `DocumentService` maps extensions to S3 content types for signing
3. **Python pipeline** — `extract_handler.py` dispatches on extension to the right extractor
4. **React frontend** — `UploadDropzone` controls what the file picker and drag-and-drop accept
5. **Frontend upload hook** — `useDocuments.ts` sets the `Content-Type` header on the S3 PUT
6. **Database** — a `CHECK` constraint on `documents.file_type` enforces the allowed set at rest

Each of these is independent. A gap in any one of them causes a different failure mode: the API rejects the upload at step 1, the pipeline silently fails at step 3, the S3 PUT returns a 403 at step 5, or the database insert throws at step 6.

---

## Step 1: The API Validation Layer (C#)

The entry point is `DocumentsFunction.cs`. When a client calls `POST /documents/upload-url`, the function checks the extension against an allowed set before doing anything else:

```csharp
// Before
var allowedExtensions = new HashSet<string> { "pdf", "docx", "csv", "txt" };

// After
var allowedExtensions = new HashSet<string> { "pdf", "docx", "csv", "txt", "md" };
```

And in `DocumentService.cs`, the content-type map that drives the presigned URL signing:

```csharp
private static readonly Dictionary<string, string> ContentTypes = new()
{
    ["pdf"]  = "application/pdf",
    ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    ["csv"]  = "text/csv",
    ["txt"]  = "text/plain",
    ["md"]   = "text/markdown",   // added
};
```

The presigned URL is signed for a specific `Content-Type`. Whatever the client sends in the PUT must match exactly — S3 rejects mismatches with a 403. That matching requirement is what caused the browser bug described below.

---

## Step 2: Python Extraction

The extraction handler in `extract_handler.py` already had a clean dispatch pattern:

```python
if ext == "pdf":
    text, page_count = _extract_pdf(content)
elif ext == "docx":
    text, page_count = _extract_docx(content)
elif ext == "csv":
    text, page_count = _extract_csv(content)
elif ext == "txt":
    text       = content.decode("utf-8", errors="replace")
    page_count = 1
else:
    raise ValueError(f"Unsupported file type: {ext}")
```

Markdown is plain UTF-8 text with formatting syntax. The right extraction strategy here is just to read it as-is and let the chunker and embedding model deal with the content. The Markdown syntax (headers, bold, code fences) doesn't hurt RAG quality — the embedding model handles natural text well enough that the punctuation is just noise rather than a problem.

The change was a one-liner:

```python
elif ext in ("txt", "md"):
    text       = content.decode("utf-8", errors="replace")
    page_count = 1
```

If you wanted to strip Markdown syntax before embedding, you could run the content through a parser like `mistune` and extract just the text nodes. For the scope of this project that's premature — the current approach works and keeps the pipeline dependency-free for this case.

---

## Step 3: Frontend Dropzone

`UploadDropzone.tsx` uses the `accept` prop to tell the browser which files to allow:

```tsx
// Before
accept={{ "application/pdf": [".pdf"], "text/plain": [".txt"], ... }}

// After
accept={{ "application/pdf": [".pdf"], "text/plain": [".txt"], "text/markdown": [".md"], ... }}
```

This controls both the native file picker dialog (what's visible and selectable) and drag-and-drop validation (what gets highlighted vs. rejected). Both are client-side UX — neither is a security boundary — but they matter for usability.

---

## Step 4: The Browser MIME Type Bug

This is the part that didn't work on the first try.

When the frontend uploads a file to S3, it needs to set the `Content-Type` header to match whatever the presigned URL was signed for. The original code used `file.type` — the MIME type the browser reports for the selected file:

```ts
await axios.put(uploadUrl, file, {
  headers: { "Content-Type": file.type },
});
```

For PDFs this works fine. For `.md` files it doesn't. `file.type` for Markdown is unreliable across browsers: Chrome reports `""` (empty string), some environments report `"text/plain"`. The presigned URL was signed for `"text/markdown"`. An empty string or `"text/plain"` in the `Content-Type` header causes S3 to reject the PUT with a 403.

The fix is to not trust `file.type` at all. Instead, derive the content type from the file extension:

```ts
const MIME_MAP: Record<string, string> = {
  pdf:  "application/pdf",
  docx: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
  csv:  "text/csv",
  txt:  "text/plain",
  md:   "text/markdown",
};

function getMimeType(filename: string): string {
  const ext = filename.split(".").pop()?.toLowerCase() ?? "";
  return MIME_MAP[ext] ?? "application/octet-stream";
}

// Usage
await axios.put(uploadUrl, file, {
  headers: { "Content-Type": getMimeType(file.name) },
});
```

This makes the MIME type determination consistent across every browser and OS, and it keeps the frontend and API in sync — both derive the content type from the same extension mapping.

The lesson here is general: `file.type` is a hint from the operating system, not a contract. For any workflow where the content type has downstream consequences (like S3 presigned URL validation), always derive it yourself from the extension.

---

## Step 5: The Schema Migration

The database had a CHECK constraint on `documents.file_type` from the initial schema:

```sql
CHECK (file_type IN ('pdf', 'csv', 'docx', 'txt'))
```

Without updating this, every Markdown document insert would fail with a constraint violation — after the file had already been uploaded to S3 and the pipeline had started. The migration is straightforward:

```sql
-- migrations/002_add_md_file_type.sql
ALTER TABLE documents
  DROP CONSTRAINT documents_file_type_check,
  ADD  CONSTRAINT documents_file_type_check
       CHECK (file_type IN ('pdf', 'csv', 'docx', 'txt', 'md'));
```

Drop the old constraint, add the new one. Because Aurora Serverless v2 is the backing store and this is a DDL statement with no data rewrite, it completes nearly instantly regardless of table size.

The migration is applied via `scripts/migrate-local.py` against the RDS Data API. No VPN, no bastion host — just a boto3 `execute_statement` call.

---

## What the Change Looks Like End-to-End

Here's the complete path for a Markdown upload after all the changes:

1. User drops `README.md` onto the dropzone — accepted because `text/markdown` is in the `accept` map
2. Frontend calls `POST /documents/upload-url` with `{ fileName: "README.md", fileType: "md" }`
3. C# API validates `"md"` against the allowed set, maps it to `text/markdown`, issues a presigned S3 PUT URL
4. Frontend PUTs the file to S3 with `Content-Type: text/markdown` derived from the extension map
5. S3 emits an `Object Created` event → EventBridge → Step Functions
6. `ExtractText` Lambda reads the S3 object, sees extension `md`, decodes UTF-8 — done
7. Chunker, embedder, and metadata stages run unchanged — they operate on raw text regardless of source format
8. `MarkReady` sets status to `ready`; the database insert succeeds because the CHECK constraint now includes `md`
9. UI polls, sees the document flip to ready, and it's available for chat

Seven steps that could each fail independently. The layered change ensures they all agree.

---

## Why Not Just Use `.txt`?

The question comes up: since Markdown is plain text, why not just rename it to `.txt` at upload time and skip all of this?

The immediate answer is that it loses information. A `.txt` file and a Markdown file aren't the same thing — Markdown has structure (headers, lists, code blocks) that could eventually be used to improve chunking or embedding quality. Stripping it at upload time forecloses that option.

The deeper answer is that the explicit `md` type in the database lets you query by format later. If you want to add a Markdown-aware chunker that splits on heading boundaries instead of character windows, you can target those documents specifically. A generic `txt` label makes that kind of targeted improvement impossible without re-classifying every document.

---

## What This Pattern Looks Like for the Next Format

If you wanted to add EPUB or HTML support, the same checklist applies:

- [ ] Add the extension to the C# allowed set in `DocumentsFunction.cs`
- [ ] Add the MIME type mapping in `DocumentService.cs`
- [ ] Add an extraction branch in `extract_handler.py`
- [ ] Add the MIME type to the frontend `accept` map in `UploadDropzone.tsx`
- [ ] Add the extension to the MIME map in `useDocuments.ts`
- [ ] Write a migration to extend the `file_type` CHECK constraint

Each layer is independently responsible for its concern. The checklist is mechanical, but that's actually the goal — a new file type shouldn't require rethinking the architecture.

---

## Links

- [Part 1: Architecture Overview](./01-architecture.md)
- [Part 2: Multi-Tenant Auth](./02-auth-multitenant.md)
- [Part 3: Document Pipeline](./03-step-functions-pipeline.md)
- [Part 4: RAG with pgvector](./04-rag-pgvector.md)
- [Part 5: React Frontend](./05-react-frontend.md)
- [Part 6: Zero-Secret CI/CD](./06-cicd.md)
