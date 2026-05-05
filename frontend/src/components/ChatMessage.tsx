import { Bot, User } from 'lucide-react'
import { cn } from '@/lib/utils'
import { type Citation } from '@/api/client'

export interface Message {
  role:      'user' | 'assistant'
  content:   string
  citations?: Citation[]
}

interface ChatMessageProps {
  message: Message
}

export function ChatMessage({ message }: ChatMessageProps) {
  const isUser = message.role === 'user'

  return (
    <div className={cn('flex gap-3 items-start', isUser && 'flex-row-reverse')}>
      <div className={cn(
        'flex h-8 w-8 shrink-0 items-center justify-center rounded-full',
        isUser ? 'bg-blue-600 text-white' : 'bg-slate-100 text-slate-600'
      )}>
        {isUser ? <User className="h-4 w-4" /> : <Bot className="h-4 w-4" />}
      </div>

      <div className={cn('max-w-[80%] space-y-2', isUser && 'items-end')}>
        <div className={cn(
          'rounded-2xl px-4 py-2.5 text-sm',
          isUser
            ? 'bg-blue-600 text-white rounded-tr-sm'
            : 'bg-slate-100 text-slate-900 rounded-tl-sm'
        )}>
          {message.content}
        </div>

        {message.citations && message.citations.length > 0 && (
          <div className="space-y-1">
            <p className="text-xs text-slate-400 px-1">Sources:</p>
            {message.citations.map((c, i) => (
              <div
                key={i}
                className="rounded-md border border-slate-200 bg-white px-3 py-2 text-xs text-slate-600"
              >
                <span className="font-medium text-blue-600">[{i + 1}]</span>{' '}
                <span className="font-medium">{c.filename}</span>
                <span className="text-slate-400"> · chunk {c.chunkIndex}</span>
                <p className="mt-1 text-slate-500 line-clamp-2 italic">"{c.excerpt}"</p>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}
