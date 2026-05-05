# Article 5: Building the React Frontend — Document Library + Chat UI

**Tagline:** React Query's refetchInterval turns a polling requirement into a one-liner. Here's the whole frontend, explained.

---

## Outline

### Hook
The frontend is where the demo either lands or doesn't. Interviewers will click around. The uploads need to feel instant, the pipeline status needs to update without a refresh, and the chat needs citations that prove the AI actually read your documents.

### Tech choices
- **Vite** — faster than Create React App, native ES modules
- **React 18 + TypeScript** — strict mode, zero `any`
- **Tailwind v4** — utility-first, no CSS files to maintain
- **React Query** — server state management, auto-polling, cache invalidation
- **Amplify Auth** — Cognito Hosted UI in ~10 lines, handles PKCE + token refresh
- **Axios** — request interceptor injects Bearer token automatically

### Part 1: Auth with Amplify

Walk through `auth/cognito.ts`:
- `configureAmplify()` reads VITE_ env vars at startup
- `getAccessToken()` called from Axios interceptor — always fresh, handles refresh automatically
- `Authenticator` wrapper component handles the full sign-in/sign-up/confirm flow without custom UI

Show the App.tsx wrapping pattern and the `tenantApi.sync()` call on first load that upserts the user row.

### Part 2: Document upload flow

**Presigned URL pattern**
Explain why we don't upload through API Gateway:
1. `POST /documents/upload-url` → Lambda creates DB record + returns S3 presigned PUT URL
2. React PUTs the file directly to S3 (bypasses API Gateway's 10MB payload limit)
3. S3 ObjectCreated → EventBridge → Step Functions (pipeline starts automatically)

Walk through `UploadDropzone.tsx` — drag-and-drop, file type validation, progress state.
Walk through `useUploadDocument` hook — the two-step mutate.

**Why create the DB record first?**
The pipeline needs a document ID to update status. Creating it before upload ensures the record exists when the Step Functions execution starts.

### Part 3: Status polling

Walk through `useDocuments.ts`:
```typescript
refetchInterval: (query) => {
  const docs = query.state.data ?? []
  return docs.some(d => d.status === 'pending' || d.status === 'processing')
    ? 3000 : false
},
```
React Query polls every 3s while any document is in-flight, stops automatically when all are `ready` or `failed`. No WebSocket needed.

Walk through `DocumentCard.tsx`:
- Status badge with animated spinner during processing
- Summary + topic chips appear once processing completes
- Error message shown inline on failed documents

### Part 4: Chat UI

Walk through `useChat.ts` — local state machine: messages array, loading flag, error state.
Walk through `ChatPage.tsx`:
- `useEffect` + `scrollIntoView` for auto-scroll
- Input disabled during loading
- "Thinking…" indicator while awaiting response

Walk through `ChatMessage.tsx`:
- User messages right-aligned (blue bubble)
- Assistant messages left-aligned (grey bubble)
- Citations rendered as cards below the answer with filename, chunk index, and excerpt

### Part 5: Tenant settings

Walk through `SettingsPage.tsx` — tenant info card, user list with role toggle.
Show the `useMutation` + `useQueryClient.invalidateQueries` pattern for optimistic-ish role updates.

### Part 6: What's missing for production
- Error boundaries
- Pagination on document list
- File size validation before requesting upload URL
- Streaming chat responses (Lambda Function URLs)
- E2E tests (Playwright)

---

## Key code references
- `frontend/src/auth/cognito.ts` — Amplify config
- `frontend/src/api/client.ts` — Axios + auth interceptor + typed API methods
- `frontend/src/hooks/useDocuments.ts` — React Query with auto-polling
- `frontend/src/components/UploadDropzone.tsx` — drag-and-drop upload
- `frontend/src/pages/Chat.tsx` — chat thread + input
- `frontend/src/components/ChatMessage.tsx` — citations UI
