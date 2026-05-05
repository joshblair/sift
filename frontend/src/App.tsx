import { useEffect, useState } from 'react'
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { Authenticator } from '@aws-amplify/ui-react'
import '@aws-amplify/ui-react/styles.css'
import { configureAmplify } from '@/auth/cognito'
import { tenantApi } from '@/api/client'
import { Layout } from '@/components/Layout'
import { DocumentsPage } from '@/pages/Documents'
import { ChatPage } from '@/pages/Chat'
import { SettingsPage } from '@/pages/Settings'
import { Spinner } from '@/components/ui/spinner'

configureAmplify()

const queryClient = new QueryClient({
  defaultOptions: {
    queries: { retry: 1, staleTime: 30_000 },
  },
})

function AppRoutes() {
  const [synced, setSynced] = useState(false)

  // Upsert user row on every sign-in so the DB record stays current.
  useEffect(() => {
    tenantApi.sync().finally(() => setSynced(true))
  }, [])

  if (!synced) {
    return (
      <div className="min-h-screen flex items-center justify-center">
        <Spinner className="h-8 w-8" />
      </div>
    )
  }

  return (
    <BrowserRouter>
      <Routes>
        <Route element={<Layout />}>
          <Route index element={<Navigate to="/documents" replace />} />
          <Route path="documents" element={<DocumentsPage />} />
          <Route path="chat"      element={<ChatPage />} />
          <Route path="settings"  element={<SettingsPage />} />
        </Route>
      </Routes>
    </BrowserRouter>
  )
}

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <Authenticator>
        {() => <AppRoutes />}
      </Authenticator>
    </QueryClientProvider>
  )
}
