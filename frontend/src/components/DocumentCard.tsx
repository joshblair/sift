import { FileText, Trash2, Clock, CheckCircle2, XCircle, Loader2 } from 'lucide-react'
import { type Document } from '@/api/client'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardFooter } from '@/components/ui/card'
import { cn } from '@/lib/utils'

interface DocumentCardProps {
  doc:      Document
  onDelete: (id: string) => void
}

const statusConfig = {
  pending:    { label: 'Pending',    variant: 'secondary', icon: Clock },
  processing: { label: 'Processing', variant: 'warning',   icon: Loader2 },
  ready:      { label: 'Ready',      variant: 'success',   icon: CheckCircle2 },
  failed:     { label: 'Failed',     variant: 'destructive', icon: XCircle },
} as const

export function DocumentCard({ doc, onDelete }: DocumentCardProps) {
  const { label, variant, icon: Icon } = statusConfig[doc.status]

  return (
    <Card className="hover:shadow-md transition-shadow">
      <CardContent className="pt-6">
        <div className="flex items-start gap-3">
          <FileText className="h-8 w-8 text-blue-500 shrink-0 mt-0.5" />
          <div className="flex-1 min-w-0">
            <p className="font-medium text-slate-900 truncate">{doc.filename}</p>
            <p className="text-xs text-slate-500 mt-0.5">
              {doc.fileType.toUpperCase()}
              {doc.pageCount ? ` · ${doc.pageCount} pages` : ''}
              {doc.chunkCount ? ` · ${doc.chunkCount} chunks` : ''}
            </p>
          </div>
          <Badge variant={variant} className="shrink-0 flex items-center gap-1">
            <Icon className={cn('h-3 w-3', doc.status === 'processing' && 'animate-spin')} />
            {label}
          </Badge>
        </div>

        {doc.summary && (
          <p className="mt-3 text-sm text-slate-600 line-clamp-2">{doc.summary}</p>
        )}

        {doc.topics && doc.topics.length > 0 && (
          <div className="mt-2 flex flex-wrap gap-1">
            {doc.topics.map(t => (
              <Badge key={t} variant="outline" className="text-xs">{t}</Badge>
            ))}
          </div>
        )}

        {doc.status === 'failed' && doc.errorMessage && (
          <p className="mt-2 text-xs text-red-600 bg-red-50 rounded p-2">{doc.errorMessage}</p>
        )}
      </CardContent>

      <CardFooter className="justify-between text-xs text-slate-400">
        <span>{new Date(doc.createdAt).toLocaleDateString()}</span>
        <Button
          variant="ghost"
          size="icon"
          className="h-7 w-7 text-slate-400 hover:text-red-500"
          onClick={() => onDelete(doc.id)}
        >
          <Trash2 className="h-4 w-4" />
        </Button>
      </CardFooter>
    </Card>
  )
}
