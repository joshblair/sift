import { cn } from '@/lib/utils'

type BadgeVariant = 'default' | 'secondary' | 'destructive' | 'outline' | 'success' | 'warning'

interface BadgeProps extends React.HTMLAttributes<HTMLDivElement> {
  variant?: BadgeVariant
}

const variantClasses: Record<BadgeVariant, string> = {
  default:     'bg-blue-100 text-blue-800',
  secondary:   'bg-slate-100 text-slate-800',
  destructive: 'bg-red-100 text-red-800',
  outline:     'border border-slate-200 text-slate-700',
  success:     'bg-green-100 text-green-800',
  warning:     'bg-yellow-100 text-yellow-800',
}

export function Badge({ className, variant = 'default', ...props }: BadgeProps) {
  return (
    <div
      className={cn(
        'inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-semibold',
        variantClasses[variant],
        className
      )}
      {...props}
    />
  )
}
