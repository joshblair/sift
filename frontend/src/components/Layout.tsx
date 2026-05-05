import { NavLink, Outlet } from 'react-router-dom'
import { Files, MessageSquare, Settings, LogOut, Zap } from 'lucide-react'
import { signOut } from '@/auth/cognito'
import { cn } from '@/lib/utils'

const navItems = [
  { to: '/documents', label: 'Documents', icon: Files },
  { to: '/chat',      label: 'Chat',      icon: MessageSquare },
  { to: '/settings',  label: 'Settings',  icon: Settings },
]

export function Layout() {
  return (
    <div className="min-h-screen bg-slate-50 flex">
      {/* Sidebar */}
      <aside className="w-56 shrink-0 bg-white border-r flex flex-col">
        <div className="px-4 py-5 border-b">
          <div className="flex items-center gap-2">
            <Zap className="h-5 w-5 text-blue-600" />
            <span className="font-semibold text-slate-900">Sift</span>
          </div>
          <p className="text-xs text-slate-400 mt-0.5">Document Intelligence</p>
        </div>

        <nav className="flex-1 px-2 py-3 space-y-0.5">
          {navItems.map(({ to, label, icon: Icon }) => (
            <NavLink
              key={to}
              to={to}
              className={({ isActive }) => cn(
                'flex items-center gap-2.5 px-3 py-2 rounded-md text-sm transition-colors',
                isActive
                  ? 'bg-blue-50 text-blue-700 font-medium'
                  : 'text-slate-600 hover:bg-slate-100'
              )}
            >
              <Icon className="h-4 w-4" />
              {label}
            </NavLink>
          ))}
        </nav>

        <div className="px-2 py-3 border-t">
          <button
            onClick={() => signOut()}
            className="flex w-full items-center gap-2.5 px-3 py-2 rounded-md text-sm
                       text-slate-500 hover:bg-slate-100 transition-colors"
          >
            <LogOut className="h-4 w-4" />
            Sign out
          </button>
        </div>
      </aside>

      {/* Main */}
      <main className="flex-1 overflow-y-auto">
        <Outlet />
      </main>
    </div>
  )
}
