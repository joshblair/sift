import { useState, useCallback } from 'react'
import { chatApi } from '@/api/client'
import { type Message } from '@/components/ChatMessage'

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
      setMessages(prev => prev.slice(0, -1))
    } finally {
      setIsLoading(false)
    }
  }, [])

  const clearMessages = useCallback(() => {
    setMessages([])
    setError(null)
  }, [])

  return { messages, isLoading, error, sendMessage, clearMessages }
}
