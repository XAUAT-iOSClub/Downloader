import { useCallback, useEffect, useState, type FormEvent } from 'react'
import {
  Check,
  Copy,
  KeyRound,
  LoaderCircle,
  MoreVertical,
  Plus,
  Power,
  RotateCcw,
  ShieldCheck,
  Trash2,
  TriangleAlert,
} from 'lucide-react'
import { useAuth } from '@/auth/useAuth'
import { Button } from '@/components/ui/button'
import {
  createApiKey,
  deleteApiKey,
  getApiKeyList,
  setApiKeyStatus,
} from '@/services'
import type { ApiKeyCreatedDto, ApiKeyDto } from '@/types'
import type { LoadState } from '../pageComponents'
import { StatusBadge } from '../pageComponents'
import {
  AdminPageHeader,
  AdminPanel,
  ResourceContent,
  TableShell,
} from './AdminShared'
import { asLoadState, formatDate, formatDateTime, getAdminErrorMessage } from './adminUtils'
import { useAdminAuthFailure } from './useAdminAuthFailure'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"

export function AdminApiKeysPage() {
  const { token } = useAuth()
  const handleAuthFailure = useAdminAuthFailure()
  const [state, setState] = useState<LoadState<ApiKeyDto[]>>({ status: 'loading' })

  // 创建
  const [name, setName] = useState('')
  const [expireDays, setExpireDays] = useState('')
  const [createOpen, setCreateOpen] = useState(false)
  const [createSubmitting, setCreateSubmitting] = useState(false)
  const [createError, setCreateError] = useState('')

  // 创建成功后展示明文密钥，只展示这一次
  const [createdKey, setCreatedKey] = useState<ApiKeyCreatedDto | null>(null)
  const [copied, setCopied] = useState(false)

  // 启停
  const [statusPendingId, setStatusPendingId] = useState<string | null>(null)

  // 删除
  const [deleteTarget, setDeleteTarget] = useState<ApiKeyDto | null>(null)

  const loadApiKeys = useCallback(async () => {
    if (!token) return

    setState({ status: 'loading' })
    try {
      const apiKeys = await getApiKeyList(token)
      setState(asLoadState(apiKeys))
    } catch (error) {
      if (handleAuthFailure(error)) return
      setState({ status: 'error', message: getAdminErrorMessage(error) })
    }
  }, [handleAuthFailure, token])

  useEffect(() => {
    const sessionToken = token
    if (!sessionToken) return

    let ignore = false
    async function loadInitialApiKeys(sessionToken: string) {
      try {
        const apiKeys = await getApiKeyList(sessionToken)
        if (ignore) return
        setState(asLoadState(apiKeys))
      } catch (error) {
        if (ignore) return
        if (handleAuthFailure(error)) return
        setState({ status: 'error', message: getAdminErrorMessage(error) })
      }
    }
    void loadInitialApiKeys(sessionToken)
    return () => { ignore = true }
  }, [handleAuthFailure, token])

  async function handleCreateApiKey(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!token) return

    const nextName = name.trim()
    if (!nextName) {
      setCreateError('请输入密钥名称，便于日后辨认。')
      return
    }

    const parsedExpireDays = expireDays.trim() === '' ? null : Number(expireDays)
    if (parsedExpireDays !== null && (!Number.isInteger(parsedExpireDays) || parsedExpireDays <= 0)) {
      setCreateError('有效天数需要是大于 0 的整数，留空表示永不过期。')
      return
    }

    setCreateSubmitting(true)
    setCreateError('')

    try {
      const created = await createApiKey({ name: nextName, expireDays: parsedExpireDays }, token)
      setName('')
      setExpireDays('')
      setCreateOpen(false)
      setCopied(false)
      setCreatedKey(created)
      await loadApiKeys()
    } catch (error) {
      if (handleAuthFailure(error)) return
      setCreateError(getAdminErrorMessage(error))
    } finally {
      setCreateSubmitting(false)
    }
  }

  async function handleToggleStatus(apiKey: ApiKeyDto) {
    if (!token) return

    setStatusPendingId(apiKey.id)
    try {
      await setApiKeyStatus(apiKey.id, !apiKey.isActive, token)
      await loadApiKeys()
    } catch (error) {
      if (handleAuthFailure(error)) return
    } finally {
      setStatusPendingId(null)
    }
  }

  async function handleDeleteApiKey() {
    if (!token || !deleteTarget) return
    try {
      await deleteApiKey(deleteTarget.id, token)
      await loadApiKeys()
    } catch (error) {
      if (handleAuthFailure(error)) return
    } finally {
      setDeleteTarget(null)
    }
  }

  async function handleCopyKey() {
    if (!createdKey) return

    try {
      await navigator.clipboard.writeText(createdKey.key)
      setCopied(true)
    } catch {
      setCopied(false)
    }
  }

  return (
    <div className="space-y-6">
      <AdminPageHeader
        eyebrow={<><KeyRound className="size-3.5" /> API Keys</>}
        title="API 密钥"
        description="用于自动化发布：新增版本、上传安装包。密钥只授权发布操作，无法管理用户、渠道或删除数据。"
        action={
          <div className="flex gap-2">
            <Button variant="outline" size="icon" onClick={() => void loadApiKeys()}><RotateCcw className="size-4" /></Button>
            <Dialog open={createOpen} onOpenChange={setCreateOpen}>
              <DialogTrigger asChild>
                <Button className="rounded-xl shadow-apple-md"><Plus className="size-4" /> 创建密钥</Button>
              </DialogTrigger>
              <DialogContent className="sm:max-w-[425px]">
                <form onSubmit={handleCreateApiKey}>
                  <DialogHeader>
                    <DialogTitle>创建 API 密钥</DialogTitle>
                    <DialogDescription>密钥明文只在创建成功后显示一次，请及时保存到 CI 的 Secret 中。</DialogDescription>
                  </DialogHeader>
                  <div className="grid gap-4 py-4">
                    <div className="grid gap-2">
                      <Label htmlFor="api-key-name">密钥名称</Label>
                      <Input id="api-key-name" placeholder="例如 GitHub Actions 发布" value={name} onChange={(e) => setName(e.target.value)} />
                    </div>
                    <div className="grid gap-2">
                      <Label htmlFor="api-key-expire">有效天数（可选）</Label>
                      <Input
                        id="api-key-expire"
                        inputMode="numeric"
                        placeholder="留空表示永不过期"
                        value={expireDays}
                        onChange={(e) => setExpireDays(e.target.value)}
                      />
                    </div>
                    {createError && <p className="text-sm font-medium text-destructive">{createError}</p>}
                  </div>
                  <DialogFooter>
                    <Button type="submit" disabled={createSubmitting}>
                      {createSubmitting && <LoaderCircle className="mr-2 size-4 animate-spin" />} 确认创建
                    </Button>
                  </DialogFooter>
                </form>
              </DialogContent>
            </Dialog>
          </div>
        }
      />

      <AdminPanel
        title="调用方式"
        description="在请求头中携带密钥即可调用发布接口，无需登录换取 JWT。"
        icon={<ShieldCheck className="size-4" />}
      >
        <pre className="overflow-x-auto rounded-xl border border-border/60 bg-muted/40 p-4 text-xs leading-6">
{`# 新增版本
curl -X POST https://<你的域名>/api/Release \\
  -H "X-API-Key: dlk_xxxxxxxx" \\
  -H "Content-Type: application/json" \\
  -d '{"name":"1.2.0","description":"...","releaseId":"1.2.0","appId":"<应用 ID>"}'

# 上传安装包
curl -X POST https://<你的域名>/api/Soft/upload \\
  -H "X-API-Key: dlk_xxxxxxxx" \\
  -F "name=1.2.0-android" -F "releaseId=<版本 ID>" \\
  -F "channelId=<渠道 ID>" -F "file=@app-release.apk"`}
        </pre>
      </AdminPanel>

      <ResourceContent
        state={state}
        emptyTitle="暂无 API 密钥"
        emptyDescription="创建密钥后，列表会显示在这里。"
        errorTitle="密钥加载失败"
        loadingColumns={4}
      >
        {(apiKeys) => (
          <ApiKeyTable
            apiKeys={apiKeys}
            statusPendingId={statusPendingId}
            onToggleStatus={(apiKey) => void handleToggleStatus(apiKey)}
            onDelete={setDeleteTarget}
          />
        )}
      </ResourceContent>

      {/* 明文密钥只在这里出现一次 */}
      <Dialog open={!!createdKey} onOpenChange={(open) => !open && setCreatedKey(null)}>
        <DialogContent className="sm:max-w-[520px]">
          <DialogHeader>
            <DialogTitle>密钥创建成功</DialogTitle>
            <DialogDescription>请立即复制并妥善保存，关闭后无法再次查看。</DialogDescription>
          </DialogHeader>
          <div className="grid gap-4 py-4">
            <div className="flex items-start gap-2 rounded-xl border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive">
              <TriangleAlert className="mt-0.5 size-4 shrink-0" />
              <p>这串密钥等同于发布权限，请勿提交到代码仓库或写入日志。</p>
            </div>
            <div className="grid gap-2">
              <Label htmlFor="api-key-value">密钥明文</Label>
              <div className="flex gap-2">
                <Input id="api-key-value" readOnly value={createdKey?.key ?? ''} className="font-mono text-xs" onFocus={(e) => e.target.select()} />
                <Button type="button" variant="outline" onClick={() => void handleCopyKey()}>
                  {copied ? <><Check className="size-4" /> 已复制</> : <><Copy className="size-4" /> 复制</>}
                </Button>
              </div>
            </div>
          </div>
          <DialogFooter>
            <Button type="button" onClick={() => setCreatedKey(null)}>我已保存</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <AlertDialog open={!!deleteTarget} onOpenChange={(open) => !open && setDeleteTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>确认删除密钥？</AlertDialogTitle>
            <AlertDialogDescription>
              删除后「{deleteTarget?.name}」将立即失效，正在使用它的自动化流程会开始返回 401。此操作无法撤销。
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>取消</AlertDialogCancel>
            <AlertDialogAction onClick={handleDeleteApiKey} className="bg-destructive text-destructive-foreground hover:bg-destructive/90">确认删除</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  )
}

function ApiKeyTable({
  apiKeys,
  statusPendingId,
  onToggleStatus,
  onDelete,
}: {
  apiKeys: ApiKeyDto[]
  statusPendingId: string | null
  onToggleStatus: (apiKey: ApiKeyDto) => void
  onDelete: (apiKey: ApiKeyDto) => void
}) {
  return (
    <TableShell minWidth={880}>
      <thead className="bg-muted/50 text-xs font-medium text-muted-foreground">
        <tr>
          <th className="px-4 py-3 text-left">名称</th>
          <th className="px-4 py-3 text-left">密钥前缀</th>
          <th className="px-4 py-3 text-left">状态</th>
          <th className="px-4 py-3 text-left">创建时间</th>
          <th className="px-4 py-3 text-left">最后使用</th>
          <th className="px-4 py-3 text-left">过期时间</th>
          <th className="px-4 py-3 text-right">操作</th>
        </tr>
      </thead>
      <tbody className="divide-y divide-border/40">
        {apiKeys.map((apiKey) => (
          <tr key={apiKey.id} className="hover:bg-muted/30 transition-colors">
            <td className="px-4 py-3">
              <div className="font-medium text-foreground">{apiKey.name}</div>
              <div className="text-xs text-muted-foreground">{apiKey.username || '未知用户'}</div>
            </td>
            <td className="px-4 py-3 font-mono text-xs text-muted-foreground">{apiKey.prefix}…</td>
            <td className="px-4 py-3">
              {apiKey.isExpired
                ? <span className="inline-flex items-center rounded-full bg-muted px-3 py-1 text-xs font-medium text-muted-foreground">已过期</span>
                : <StatusBadge active={apiKey.isActive} />}
            </td>
            <td className="px-4 py-3 text-muted-foreground">{formatDate(apiKey.createdAt)}</td>
            <td className="px-4 py-3 text-muted-foreground">{formatDateTime(apiKey.lastUsedAt)}</td>
            <td className="px-4 py-3 text-muted-foreground">{apiKey.expiresAt ? formatDateTime(apiKey.expiresAt) : '永不过期'}</td>
            <td className="px-4 py-3 text-right">
              <DropdownMenu>
                <DropdownMenuTrigger asChild>
                  <Button variant="ghost" size="icon" className="h-8 w-8" disabled={statusPendingId === apiKey.id}>
                    {statusPendingId === apiKey.id
                      ? <LoaderCircle className="size-4 animate-spin" />
                      : <MoreVertical className="size-4" />}
                  </Button>
                </DropdownMenuTrigger>
                <DropdownMenuContent align="end">
                  <DropdownMenuItem onClick={() => onToggleStatus(apiKey)}>
                    <Power className="mr-2 size-4" /> {apiKey.isActive ? '停用' : '启用'}
                  </DropdownMenuItem>
                  <DropdownMenuItem className="text-destructive focus:text-destructive" onClick={() => onDelete(apiKey)}>
                    <Trash2 className="mr-2 size-4" /> 删除
                  </DropdownMenuItem>
                </DropdownMenuContent>
              </DropdownMenu>
            </td>
          </tr>
        ))}
      </tbody>
    </TableShell>
  )
}
