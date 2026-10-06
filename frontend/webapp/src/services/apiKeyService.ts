import type { ApiKeyCreateDto, ApiKeyCreatedDto, ApiKeyDto } from '@/types'
import { apiRequest } from './http'

const apiKeyPath = '/api/ApiKey'

export function getApiKeyList(token?: string) {
  return apiRequest<ApiKeyDto[]>(apiKeyPath, { token })
}

export function createApiKey(dto: ApiKeyCreateDto, token?: string) {
  return apiRequest<ApiKeyCreatedDto>(apiKeyPath, {
    method: 'POST',
    body: dto,
    token,
  })
}

export function setApiKeyStatus(id: string, isActive: boolean, token?: string) {
  return apiRequest<void>(`${apiKeyPath}/${encodeURIComponent(id)}/status`, {
    method: 'PUT',
    body: { isActive },
    token,
  })
}

export function deleteApiKey(id: string, token?: string) {
  return apiRequest<void>(`${apiKeyPath}/${encodeURIComponent(id)}`, {
    method: 'DELETE',
    token,
  })
}
