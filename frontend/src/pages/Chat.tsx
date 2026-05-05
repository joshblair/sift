import { useState, useRef, useEffect } from 'react'
import { MessageSquare, Send, Trash2 } from 'lucide-react'
import { ChatMessage } from '@/components/ChatMessage'
import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
import { useChat } from '@/hooks/useChat'

export function ChatPage() {
  const { messages, isLoading, error, sendMessage, clearMessages } = useChat()
  const [input, setInput]   = useState('')
  const bottomRef           = useRef<HTMLDivElement>(null)

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages, isLoading])

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    const q = input.trim()
    if (!q || isLoading) return
    setInput('')
    await sendMessage(q)
  }

  return (
    <div className="flex flex-col h-[calc(100vh-64px)] max-w-3xl mx-auto">
      {/* Header */}
      <div className="flex items-center justify-between px-6 py-4 border-b">
        <div className="flex items-center gap-2">
          <MessageSquare className="h-5 w-5 text-blue-600" />
          <h1 className="text-lg font-semibold text-slate-900">Chat with your documents</h1>
        </div>
        {messages.length > 0 && (
          <Button variant="ghost" size="sm" onClick={clearMessages} className="text-slate-400 gap-1.5">
            <Trash2 className="h-3.5 w-3.5" />
            Clear
          </Button>
        )}
      </div>

      {/* Messages */}
      <div className="flex-1 overflow-y-auto px-6 py-4 space-y-4">
        {messages.length === 0 ? (
          <div className="h-full flex items-center justify-center">
            <div className="text-center text-slate-400">
              <MessageSquare className="mx-auto h-12 w-12 mb-3 opacity-30" />
              <p className="font-medium">Ask anything about your documents</p>
              <p className="text-sm mt-1">Answers include citations linking back to the source chunks.</p>
            </div>
          </div>
        ) : (
          messages.map((msg, i) => <ChatMessage key={i} message={msg} />)
        )}

        {isLoading && (
          <div className="flex gap-3 items-center">
            <div className="flex h-8 w-8 items-center justify-center rounded-full bg-slate-100">
              <Spinner />
            </div>
            <span className="text-sm text-slate-400">Thinking…</span>
          </div>
        )}

        {error && (
          <p className="text-center text-sm text-red-500">{error}</p>
        )}

        <div ref={bottomRef} />
      </div>

      {/* Input */}
      <form onSubmit={handleSubmit} className="px-6 py-4 border-t flex gap-2">
        <input
          value={input}
          onChange={e => setInput(e.target.value)}
          placeholder="Ask a question about your documents…"
          disabled={isLoading}
          className="flex-1 rounded-md border border-slate-200 px-3 py-2 text-sm
                     focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-50"
        />
        <Button type="submit" disabled={!input.trim() || isLoading} size="icon">
          <Send className="h-4 w-4" />
        </Button>
      </form>
    </div>
  )
}
