import { Files } from 'lucide-react'
import { DocumentCard } from '@/components/DocumentCard'
import { UploadDropzone } from '@/components/UploadDropzone'
import { Spinner } from '@/components/ui/spinner'
import { useDocuments, useDeleteDocument, useUploadDocument } from '@/hooks/useDocuments'

export function DocumentsPage() {
  const { data: docs = [], isLoading }  = useDocuments()
  const deleteMutation                  = useDeleteDocument()
  const uploadMutation                  = useUploadDocument()

  return (
    <div className="p-6 max-w-4xl mx-auto space-y-6">
      <div className="flex items-center gap-2">
        <Files className="h-6 w-6 text-blue-600" />
        <h1 className="text-2xl font-semibold text-slate-900">Documents</h1>
        {isLoading && <Spinner className="ml-2" />}
      </div>

      <UploadDropzone onUpload={file => uploadMutation.mutateAsync(file)} />

      {docs.length === 0 && !isLoading ? (
        <div className="text-center py-16 text-slate-400">
          <Files className="mx-auto h-12 w-12 mb-3 opacity-30" />
          <p>No documents yet. Upload one above to get started.</p>
        </div>
      ) : (
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
          {docs.map(doc => (
            <DocumentCard
              key={doc.id}
              doc={doc}
              onDelete={id => deleteMutation.mutate(id)}
            />
          ))}
        </div>
      )}
    </div>
  )
}
