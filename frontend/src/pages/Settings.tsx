import { useState } from 'react'
import { Settings, Users, Shield } from 'lucide-react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { tenantApi, type User } from '@/api/client'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Spinner } from '@/components/ui/spinner'

export function SettingsPage() {
  const queryClient                              = useQueryClient()
  const { data: tenant }                         = useQuery({ queryKey: ['tenant'], queryFn: tenantApi.getTenant })
  const { data: users = [], isLoading: usersLoading } = useQuery({ queryKey: ['users'], queryFn: tenantApi.listUsers })
  const [updatingId, setUpdatingId]              = useState<string | null>(null)

  const roleMutation = useMutation({
    mutationFn: ({ userId, role }: { userId: string; role: string }) =>
      tenantApi.updateRole(userId, role),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['users'] }),
  })

  const toggleRole = async (user: User) => {
    setUpdatingId(user.id)
    const newRole = user.role === 'admin' ? 'member' : 'admin'
    await roleMutation.mutateAsync({ userId: user.id, role: newRole })
    setUpdatingId(null)
  }

  return (
    <div className="p-6 max-w-3xl mx-auto space-y-6">
      <div className="flex items-center gap-2">
        <Settings className="h-6 w-6 text-blue-600" />
        <h1 className="text-2xl font-semibold text-slate-900">Settings</h1>
      </div>

      {/* Tenant info */}
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Shield className="h-4 w-4" />
            Tenant
          </CardTitle>
        </CardHeader>
        <CardContent>
          {tenant ? (
            <div className="space-y-1 text-sm">
              <div className="flex gap-2">
                <span className="text-slate-500 w-16">Name</span>
                <span className="font-medium">{tenant.name}</span>
              </div>
              <div className="flex gap-2">
                <span className="text-slate-500 w-16">Slug</span>
                <code className="text-xs bg-slate-100 px-1.5 py-0.5 rounded">{tenant.slug}</code>
              </div>
              <div className="flex gap-2">
                <span className="text-slate-500 w-16">ID</span>
                <code className="text-xs bg-slate-100 px-1.5 py-0.5 rounded">{tenant.id}</code>
              </div>
            </div>
          ) : (
            <Spinner />
          )}
        </CardContent>
      </Card>

      {/* Users */}
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Users className="h-4 w-4" />
            Users
            {usersLoading && <Spinner className="ml-1" />}
          </CardTitle>
        </CardHeader>
        <CardContent>
          <div className="divide-y">
            {users.map(user => (
              <div key={user.id} className="flex items-center justify-between py-3">
                <div>
                  <p className="text-sm font-medium text-slate-900">{user.email}</p>
                  <p className="text-xs text-slate-400">
                    Joined {new Date(user.createdAt).toLocaleDateString()}
                  </p>
                </div>
                <div className="flex items-center gap-2">
                  <Badge variant={user.role === 'admin' ? 'default' : 'secondary'}>
                    {user.role}
                  </Badge>
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={updatingId === user.id}
                    onClick={() => toggleRole(user)}
                    className="text-xs"
                  >
                    {updatingId === user.id
                      ? <Spinner className="h-3 w-3" />
                      : user.role === 'admin' ? 'Make member' : 'Make admin'}
                  </Button>
                </div>
              </div>
            ))}
          </div>
        </CardContent>
      </Card>
    </div>
  )
}
