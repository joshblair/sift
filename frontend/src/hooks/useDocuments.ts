import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import axios from 'axios'
import { documentsApi, type Document } from '@/api/client'

export function useDocuments() {
  return useQuery<Document[]>({
    queryKey:    ['documents'],
    queryFn:     documentsApi.list,
    refetchInterval: (query) => {
      // Keep polling while any document is still processing
      const docs = query.state.data ?? []
      return docs.some(d => d.status === 'pending' || d.status === 'processing') ? 3000 : false
    },
  })
}

export function useDeleteDocument() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (id: string) => documentsApi.delete(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['documents'] }),
  })
}

export function useUploadDocument() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async (file: File) => {
      const ext      = file.name.split('.').pop()?.toLowerCase() ?? 'txt'
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
