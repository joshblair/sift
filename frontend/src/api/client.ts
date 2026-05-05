import axios from 'axios'
import { getAccessToken } from '@/auth/cognito'

const api = axios.create({
  baseURL: import.meta.env.VITE_API_URL,
})

api.interceptors.request.use(async (config) => {
  const token = await getAccessToken()
  config.headers.Authorization = `Bearer ${token}`
  return config
})

// ── Documents ─────────────────────────────────────────────────────────────────

export interface Document {
  id:           string
  tenantId:     string
  uploadedBy:   string
  filename:     string
  s3Key:        string
  fileType:     string
  status:       'pending' | 'processing' | 'ready' | 'failed'
  summary:      string | null
  topics:       string[] | null
  pageCount:    number | null
  chunkCount:   number | null
  errorMessage: string | null
  createdAt:    string
  processedAt:  string | null
}

export interface UploadUrlResponse {
  documentId: string
  uploadUrl:  string
  s3Key:      string
}

export const documentsApi = {
  list: ()                    => api.get<Document[]>('/documents').then(r => r.data),
  get:  (id: string)          => api.get<Document>(`/documents/${id}`).then(r => r.data),
  delete: (id: string)        => api.delete(`/documents/${id}`),
  getUploadUrl: (filename: string, fileType: string) =>
    api.post<UploadUrlResponse>('/documents/upload-url', { filename, fileType }).then(r => r.data),
}

// ── Chat ──────────────────────────────────────────────────────────────────────

export interface Citation {
  documentId: string
  filename:   string
  excerpt:    string
  chunkIndex: number
}

export interface ChatResponse {
  answer:    string
  citations: Citation[]
}

export const chatApi = {
  query: (question: string) =>
    api.post<ChatResponse>('/chat', { question }).then(r => r.data),
}

// ── Tenants ───────────────────────────────────────────────────────────────────

export interface Tenant {
  id:        string
  name:      string
  slug:      string
  createdAt: string
}

export interface User {
  id:         string
  tenantId:   string
  cognitoSub: string
  email:      string
  role:       'admin' | 'member'
  createdAt:  string
}

export const tenantApi = {
  sync:        ()                               => api.post<User>('/tenants/me/sync').then(r => r.data),
  getTenant:   ()                               => api.get<Tenant>('/tenants/me').then(r => r.data),
  listUsers:   ()                               => api.get<User[]>('/tenants/users').then(r => r.data),
  updateRole:  (userId: string, role: string)   =>
    api.put(`/tenants/users/${userId}/role`, { role }),
}

export default api
