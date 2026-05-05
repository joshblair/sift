import { useCallback, useState } from 'react'
import { Upload, X } from 'lucide-react'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'

const ACCEPTED = { 'application/pdf': ['.pdf'], 'text/plain': ['.txt'], 'text/csv': ['.csv'],
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document': ['.docx'] }
const ACCEPTED_EXT = ['.pdf', '.txt', '.csv', '.docx']

interface UploadDropzoneProps {
  onUpload: (file: File) => Promise<void>
}

export function UploadDropzone({ onUpload }: UploadDropzoneProps) {
  const [isDragging, setIsDragging] = useState(false)
  const [uploading, setUploading]   = useState(false)
  const [error, setError]           = useState<string | null>(null)

  const handleFile = useCallback(async (file: File) => {
    const ext = '.' + file.name.split('.').pop()?.toLowerCase()
    if (!ACCEPTED_EXT.includes(ext)) {
      setError(`Unsupported file type. Accepted: ${ACCEPTED_EXT.join(', ')}`)
      return
    }
    setError(null)
    setUploading(true)
    try {
      await onUpload(file)
    } catch {
      setError('Upload failed. Please try again.')
    } finally {
      setUploading(false)
    }
  }, [onUpload])

  const onDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault()
    setIsDragging(false)
    const file = e.dataTransfer.files[0]
    if (file) handleFile(file)
  }, [handleFile])

  return (
    <div
      onDragOver={e => { e.preventDefault(); setIsDragging(true) }}
      onDragLeave={() => setIsDragging(false)}
      onDrop={onDrop}
      className={cn(
        'relative border-2 border-dashed rounded-lg p-8 text-center transition-colors',
        isDragging ? 'border-blue-500 bg-blue-50' : 'border-slate-200 hover:border-slate-300',
        uploading && 'pointer-events-none opacity-70'
      )}
    >
      {uploading ? (
        <div className="flex flex-col items-center gap-2">
          <Spinner className="h-6 w-6" />
          <p className="text-sm text-slate-500">Uploading…</p>
        </div>
      ) : (
        <>
          <Upload className="mx-auto h-8 w-8 text-slate-400" />
          <p className="mt-2 text-sm text-slate-600">
            Drag & drop a file, or{' '}
            <label className="cursor-pointer text-blue-600 hover:underline">
              browse
              <input
                type="file"
                className="sr-only"
                accept={Object.values(ACCEPTED).flat().join(',')}
                onChange={e => e.target.files?.[0] && handleFile(e.target.files[0])}
              />
            </label>
          </p>
          <p className="mt-1 text-xs text-slate-400">PDF, DOCX, CSV, TXT</p>
        </>
      )}

      {error && (
        <div className="mt-3 flex items-center gap-1.5 text-xs text-red-600">
          <X className="h-3.5 w-3.5" />
          {error}
          <Button variant="ghost" size="icon" className="ml-auto h-5 w-5" onClick={() => setError(null)}>
            <X className="h-3 w-3" />
          </Button>
        </div>
      )}
    </div>
  )
}
