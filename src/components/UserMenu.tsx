import { User, Key, SignOut, Users } from '@phosphor-icons/react'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Badge } from '@/components/ui/badge'
import type { AuthenticationProvider, User as UserType } from '@/types'

interface UserMenuProps {
  user: UserType
  authMode?: AuthenticationProvider
  onChangePassword?: () => void
  onRequestPasswordReset?: () => void
  onManageUsers?: () => void
  onLogout: () => void
}

export function UserMenu({
  user,
  authMode = user.authenticationProvider ?? 'simple',
  onChangePassword,
  onRequestPasswordReset,
  onManageUsers,
  onLogout,
}: UserMenuProps) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" className="gap-2">
          <div className="w-8 h-8 rounded-full bg-primary/10 flex items-center justify-center">
            <User size={18} className="text-primary" weight="duotone" />
          </div>
          <div className="text-left">
            <div className="text-sm font-medium">{user.fullName}</div>
            <div className="text-xs text-muted-foreground">{user.department || user.role}</div>
          </div>
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-56">
        <DropdownMenuLabel>
          <div className="flex flex-col gap-1">
            <div className="flex items-center justify-between">
              <span>{user.fullName}</span>
              <Badge variant={user.role === 'admin' ? 'default' : 'secondary'} className="text-xs">
                {user.role}
              </Badge>
            </div>
            <span className="text-xs text-muted-foreground font-normal">@{user.username}</span>
          </div>
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        {authMode === 'simple' && onChangePassword && (
          <DropdownMenuItem onClick={onChangePassword}>
            <Key size={16} className="mr-2" />
            Change Password
          </DropdownMenuItem>
        )}
        {authMode === 'simple' && onRequestPasswordReset && (
          <DropdownMenuItem onClick={onRequestPasswordReset}>
            <Key size={16} className="mr-2" />
            Request Password Reset
          </DropdownMenuItem>
        )}
        {user.role === 'admin' && onManageUsers && (
          <>
            <DropdownMenuSeparator />
            <DropdownMenuItem onClick={onManageUsers}>
              <Users size={16} className="mr-2" />
              Manage Users
            </DropdownMenuItem>
          </>
        )}
        <DropdownMenuSeparator />
        <DropdownMenuItem onClick={onLogout} className="text-destructive">
          <SignOut size={16} className="mr-2" />
          Sign Out
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
