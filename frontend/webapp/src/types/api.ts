export interface AppDto {
  id: string
  name: string
  description: string
  iconUrl: string
  isActive: boolean
}

export interface AppDetailDto extends AppDto {
  releases: ReleaseDto[]
  protocols: ProtocolDto[]
}

export interface AppLatestVersionDto {
  appId: string
  appName: string
  releaseId: string
  releaseDate: string
  softs: SoftDto[]
}

export interface AppCreateDto {
  name: string
  description: string
  userId: string
}

export interface AppUpdateDto {
  name: string
  description: string
  iconUrl: string
  isActive: boolean
}

export interface ChannelDto {
  id: string
  name: string
}

export interface ChannelCreateDto {
  name: string
}

export interface ChannelUpdateDto {
  name: string
}

export interface ProtocolDto {
  id: string
  name: string
  description: string
  context: string
}

export interface ProtocolCreateDto {
  name: string
  description: string
  context: string
  appId: string
}

export interface ProtocolUpdateDto {
  name: string
  description: string
  context: string
}

export interface ReleaseDto {
  id: string
  name: string
  description: string
  releaseDate: string
  releaseId: string
  softs: SoftDto[]
}

export interface ReleaseCreateDto {
  name: string
  description: string
  releaseId: string
  appId: string
}

export interface ReleaseUpdateDto {
  name: string
  description: string
  releaseId: string
}

export interface SoftDto {
  id: string
  name: string
  softUrl: string
  description?: string
  channel: ChannelDto | null
}

export interface SoftCreateDto {
  name: string
  softUrl: string
  description?: string
  releaseId: string
  channelId: string
}

export interface SoftUpdateDto {
  name: string
  softUrl: string
  description?: string
  releaseId: string
  channelId: string
}

export interface UserDto {
  id: string
  username: string
  email: string
  emailConfirmed: boolean
  identity: string
}

export interface UserCreateDto {
  username: string
  password: string
  email: string
}

export interface UserUpdateDto {
  email: string
  identity: string
}

export interface UserChangePasswordDto {
  newPassword: string
}

export interface UserLoginDto {
  username: string
  password: string
}

export interface LoginResultDto {
  token: string
  user: UserDto
}

export interface ApiKeyDto {
  id: string
  name: string
  prefix: string
  scope: string
  userId: string
  username: string
  isActive: boolean
  createdAt: string
  expiresAt: string | null
  lastUsedAt: string | null
  isExpired: boolean
}

export interface ApiKeyCreateDto {
  name: string
  /** 有效天数，留空表示永不过期 */
  expireDays?: number | null
}

export interface ApiKeyCreatedDto extends ApiKeyDto {
  /** 密钥明文，只在创建时返回一次 */
  key: string
}

export interface ApiKeyStatusDto {
  isActive: boolean
}
