# Building the React Frontend: Document Library and Chat UI (Part 5)

*React Query's `refetchInterval` turns a polling requirement into a one-liner. Here's the whole frontend, explained.*

---

The frontend is where the demo either lands or doesn't. Interviewers will click around. Uploads need to feel instant, the pipeline status needs to update without a refresh, and the chat responses need citations that prove the AI actually read the documents — not just hallucinated plausible-sounding text.

This post covers how each of those things works, starting from auth and working through upload, status polling, and the chat UI.

---

## Tech Choices

A quick inventory before diving in:

- **Vite** — faster dev server than Create React App, native ES module HMR, straightforward to configure
- **React 18 + TypeScript** — strict mode, no `any`, every API response typed at the boundary
- **Tailwind v4** — utility-first, no separate CSS files to maintain
- **React Query (`@tanstack/react-query`)** — server state management, automatic cache invalidation, and the polling behavior discussed below
- **AWS Amplify UI** — the `Authenticator` component handles the full Cognito sign-in/sign-up flow without custom UI
- **Axios** — request interceptor injects the Bearer token on every outbound request

---

## Auth: Amplify and the Token Interceptor

`configureAmplify()` runs once at module load before anything else renders:

```typescript
export function configureAmplify() {
  Amplify.configure({
    Auth: {
      Cognito: {
        userPoolId:       import.meta.env.VITE_USER_POOL_ID,
        userPoolClientId: import.meta.env.VITE_USER_POOL_CLIENT_ID,
        loginWith: {
          oauth: {
            domain:          import.meta.env.VITE_COGNITO_DOMAIN,
            scopes:          ['email', 'openid', 'profile'],
            redirectSignIn:  [window.location.origin],
            redirectSignOut: [window.location.origin],
            responseType:    'code',
          },
        },
      },
    },
  })
}
```

All values come from `VITE_` environment variables, injected at build time from `.env.local`. The `responseType: 'code'` uses the PKCE authorization code flow — the correct flow for single-page apps, which can't keep a client secret.

The `getAccessToken` function returns the token that every API request carries:

```typescript
export async function getAccessToken(): Promise<string> {
  const session = await fetchAuthSession()
  const token   = session.tokens?.idToken?.toString()
  if (!token) throw new Error('No ID token')
  return token
}
```

Notice it's returning the **ID token**, not the access token. This is intentional and slightly non-obvious. Cognito's Pre-Token Generation V1 trigger — which injects the `tenantId` custom claim (covered in Part 2) — only applies to the ID token. The access token doesn't get custom claims from V1 triggers. Since API Gateway validates the `tenantId` claim from the token, the ID token is what needs to be sent.

Amplify handles token refresh transparently — `fetchAuthSession()` returns a fresh token if the current one is near expiry, with no code required on the caller's side.

The Axios client wires this up with a request interceptor:

```typescript
const api = axios.create({ baseURL: import.meta.env.VITE_API_URL })

api.interceptors.request.use(async (config) => {
  const token = await getAccessToken()
  config.headers.Authorization = `Bearer ${token}`
  return config
})
```

Every API call goes through this interceptor, so no individual function or component ever has to think about auth headers.

### App Initialization

`App.tsx` wraps everything in the Amplify `Authenticator` component:

```tsx
export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <Authenticator>
        {() => <AppRoutes />}
      </Authenticator>
    </QueryClientProvider>
  )
}
```

`Authenticator` renders the Cognito Hosted UI for unauthenticated users — sign-in, sign-up, email verification — and calls its render prop only after the user is authenticated. The entire application sits inside that render prop. No route guards, no redirect logic, no token-checking in individual components.

The first thing `AppRoutes` does on mount is sync the user record:

```tsx
function AppRoutes() {
  const [synced, setSynced] = useState(false)

  useEffect(() => {
    tenantApi.sync().finally(() => setSynced(true))
  }, [])

  if (!synced) return <Spinner />
  // ... routes
}
```

`POST /tenants/me/sync` upserts the user row in the database using the JWT's `sub` and `email` claims. This handles first-time logins (where no user row exists yet) and keeps the email current if it changes in Cognito. The app doesn't render routes until this completes — a brief spinner rather than a flash of potentially stale state.

---

## Document Upload: The Two-Step Flow

The upload flow has two distinct steps, and understanding why matters for the architecture.

**Why not upload through API Gateway?** API Gateway HTTP APIs have a 10MB payload limit. A single PDF can easily exceed that. The solution is to bypass API Gateway entirely for the file content and upload directly to S3.

**Step 1:** The client calls `POST /documents/upload-url` with the filename and file type. The Lambda creates the database record (status `pending`) and returns a presigned S3 PUT URL along with the new document ID.

**Step 2:** The client PUTs the file directly to the presigned URL — which goes straight to S3, bypassing API Gateway.

The `useUploadDocument` hook handles both steps:

```typescript
export function useUploadDocument() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async (file: File) => {
      const ext = file.name.split('.').pop()?.toLowerCase() ?? 'txt'
      const { uploadUrl, documentId } = await documentsApi.getUploadUrl(file.name, ext)

      // PUT directly to S3 presigned URL — bypasses API Gateway
      await axios.put(uploadUrl, file, {
        headers: { 'Content-Type': file.type },
      })

      return documentId
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['documents'] }),
  })
}
```

The second `axios.put` uses the base `axios` instance, not the `api` client with the auth interceptor. The presigned URL already has credentials embedded — adding an `Authorization` header would cause S3 to reject the request.

`onSuccess` invalidates the `documents` query, which triggers an immediate refetch. The new document appears in the list as `pending` before the pipeline has even started.

**Why create the DB record before the upload?** The S3 key is `{tenantId}/{documentId}/{filename}`. Creating the database record first gives us the document ID to construct the key. When the upload completes and EventBridge fires, the Step Functions pipeline starts immediately — and the document row it needs to update already exists.

The dropzone handles drag-and-drop and file input with consistent behavior:

```tsx
const handleFile = useCallback(async (file: File) => {
  const ext = '.' + file.name.split('.').pop()?.toLowerCase()
  if (!ACCEPTED_EXT.includes(ext)) {
    setError(`Unsupported file type. Accepted: ${ACCEPTED_EXT.join(', ')}`)
    return
  }
  setUploading(true)
  try {
    await onUpload(file)
  } catch {
    setError('Upload failed. Please try again.')
  } finally {
    setUploading(false)
  }
}, [onUpload])
```

File type validation happens client-side before any network request is made. During upload the dropzone is visually disabled (`pointer-events-none`) and shows a spinner — the user can't double-submit.

---

## Status Polling: React Query's `refetchInterval`

Once a document is uploaded, the pipeline runs asynchronously. The frontend needs to show status updates — `pending` → `processing` → `ready` — without requiring a manual refresh.

React Query's `refetchInterval` option accepts a callback that can return a number (milliseconds) or `false`:

```typescript
export function useDocuments() {
  return useQuery<Document[]>({
    queryKey:    ['documents'],
    queryFn:     documentsApi.list,
    refetchInterval: (query) => {
      const docs = query.state.data ?? []
      return docs.some(d => d.status === 'pending' || d.status === 'processing')
        ? 3000 : false
    },
  })
}
```

While any document in the list has status `pending` or `processing`, the hook polls every 3 seconds. When all documents are `ready` or `failed`, it stops. No WebSocket infrastructure, no long-polling, no `useEffect` with `setInterval`.

The `DocumentCard` component drives the status display:

```tsx
const statusConfig = {
  pending:    { label: 'Pending',    variant: 'secondary',    icon: Clock },
  processing: { label: 'Processing', variant: 'warning',      icon: Loader2 },
  ready:      { label: 'Ready',      variant: 'success',      icon: CheckCircle2 },
  failed:     { label: 'Failed',     variant: 'destructive',  icon: XCircle },
} as const
```

The processing badge uses `animate-spin` on its icon — a CSS animation that costs nothing and makes the in-progress state visually obvious. Once processing completes, the card fills in the summary paragraph and topic chips from the metadata extraction step. On failure, the error message from the `mark_failed` Lambda appears inline in a red callout:

```tsx
{doc.status === 'failed' && doc.errorMessage && (
  <p className="mt-2 text-xs text-red-600 bg-red-50 rounded p-2">{doc.errorMessage}</p>
)}
```

---

## Chat: Local State in `useChat`

The chat interface doesn't use React Query — there's no server cache to manage, just a thread of messages that grows as the user asks questions.

`useChat` is a custom hook that manages the message array, loading state, and error state:

```typescript
export function useChat() {
  const [messages, setMessages]   = useState<Message[]>([])
  const [isLoading, setIsLoading] = useState(false)
  const [error, setError]         = useState<string | null>(null)

  const sendMessage = useCallback(async (question: string) => {
    setError(null)
    setMessages(prev => [...prev, { role: 'user', content: question }])
    setIsLoading(true)

    try {
      const response = await chatApi.query(question)
      setMessages(prev => [
        ...prev,
        { role: 'assistant', content: response.answer, citations: response.citations },
      ])
    } catch {
      setError('Failed to get a response. Please try again.')
      setMessages(prev => prev.slice(0, -1))  // remove the user message on failure
    } finally {
      setIsLoading(false)
    }
  }, [])

  return { messages, isLoading, error, sendMessage, clearMessages }
}
```

The user's message is added to the thread immediately — before the API call completes — so the UI feels responsive. If the call fails, `prev.slice(0, -1)` removes it and shows an error, leaving the thread in the state it was before the failed send. This avoids the awkward state of showing a user message with no corresponding response.

The `ChatPage` scrolls to the bottom on every new message:

```tsx
const bottomRef = useRef<HTMLDivElement>(null)

useEffect(() => {
  bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
}, [messages, isLoading])
```

The ref sits on a `<div>` after the last message. `scrollIntoView` fires both when a new message arrives and when `isLoading` becomes true (so the "Thinking…" indicator is visible). The input is disabled during loading — no queuing up a second question while the first is in flight.

### Rendering Citations

`ChatMessage` handles both user and assistant messages. User messages are right-aligned in a blue bubble; assistant messages are left-aligned in grey. Citations appear as cards below the assistant's response:

```tsx
{message.citations && message.citations.length > 0 && (
  <div className="space-y-1">
    <p className="text-xs text-slate-400 px-1">Sources:</p>
    {message.citations.map((c, i) => (
      <div key={i} className="rounded-md border border-slate-200 bg-white px-3 py-2 text-xs text-slate-600">
        <span className="font-medium text-blue-600">[{i + 1}]</span>{' '}
        <span className="font-medium">{c.filename}</span>
        <span className="text-slate-400"> · chunk {c.chunkIndex}</span>
        <p className="mt-1 text-slate-500 line-clamp-2 italic">"{c.excerpt}"</p>
      </div>
    ))}
  </div>
)}
```

The `[1]`, `[2]` numbers here correspond to the same numbers Claude used inline in the answer text. The user can see "Revenue grew 18% in Q3 [1]" and then immediately read the exact sentence from the PDF that grounded that claim. `line-clamp-2` keeps the excerpt compact — two lines of truncated italic text, enough to confirm the source without overflowing the card.

---

## What's Missing for Production

The demo is complete enough to show in an interview, but a production deployment would need a few more things:

**Error boundaries.** A thrown exception anywhere in the component tree currently crashes the whole app. React error boundaries would catch rendering errors and show a recovery UI instead.

**Pagination.** The document list fetches all documents in a single request. At hundreds of documents, this becomes a performance problem — both for the API query and for rendering.

**File size validation.** The dropzone validates file type but not file size. A 500MB PDF will pass client-side validation and fail at the S3 put with an unhelpful error. A size check before requesting the upload URL is an easy addition.

**Streaming chat responses.** The chat waits for the full response before displaying anything. For longer answers this is a noticeable pause. Lambda Function URLs support HTTP response streaming, which would allow tokens to appear as they're generated. API Gateway doesn't support streaming, so this would require routing the `/chat` endpoint through a Function URL instead.

**E2E tests.** There are no Playwright or Cypress tests. For a portfolio project that's a reasonable trade-off; for a production app, automated browser tests on the upload and chat flows would be essential.

---

## What's Next

**Part 6** covers the CI/CD pipeline — GitHub Actions with OIDC federation, how the deploy workflow deploys five nested SAM stacks in dependency order, and why there are zero stored AWS credentials anywhere in the repository.

The code for this post:
- `frontend/src/auth/cognito.ts` — Amplify config, ID token vs access token
- `frontend/src/api/client.ts` — Axios client, auth interceptor, typed API surface
- `frontend/src/hooks/useDocuments.ts` — React Query with adaptive `refetchInterval`
- `frontend/src/hooks/useUploadDocument.ts` — two-step upload mutation
- `frontend/src/components/UploadDropzone.tsx` — drag-and-drop with validation
- `frontend/src/components/DocumentCard.tsx` — status badges, summary, error display
- `frontend/src/hooks/useChat.ts` — local chat state, optimistic message add
- `frontend/src/pages/Chat.tsx` — scroll behavior, loading indicator
- `frontend/src/components/ChatMessage.tsx` — citation cards

---

*Part of the Sift series: building a production-ready multi-tenant RAG platform on AWS.*
