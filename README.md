# Downloader 

一个轻量级安装中心

## 快速开始

### 后端配置

后端现在只从 [backend/Downloader.WebApi/.env](/Users/luckyfish/Documents/Project/Downloader/backend/Downloader.WebApi/.env) 读取运行时配置，并通过 [AppRuntimeConfiguration.cs](/Users/luckyfish/Documents/Project/Downloader/backend/Downloader.WebApi/Downloader.WebApi/Configuration/AppRuntimeConfiguration.cs:1) 统一加载，不再依赖 `appsettings.json` / `appsettings.Development.json` 中的业务配置。`Program.cs` 只消费这份集中配置。

可以参考 [backend/Downloader.WebApi/Downloader.WebApi/.env.example](/Users/luckyfish/Documents/Project/Downloader/backend/Downloader.WebApi/Downloader.WebApi/.env.example)：

```env
SQL=Host=127.0.0.1;Port=5432;Username=postgres;Database=downloader;Password=your-password
JwtSettings__SecretKey=ThisIsAVerySecretKeyForJwtTokenGeneration
JwtSettings__Issuer=DownloaderApp
JwtSettings__Audience=DownloaderUsers
JwtSettings__ExpireDays=7
Storage__Provider=Local
Storage__PublicBaseUrl=http://localhost:5046
```

支持的主要变量：

- `SQL` 或 `ConnectionStrings__DefaultConnection`：数据库连接字符串。
- `JwtSettings__SecretKey` / `JwtSettings__Issuer` / `JwtSettings__Audience` / `JwtSettings__ExpireDays`：JWT 配置。
- `Storage__Provider` / `Storage__PublicBaseUrl`：文件存储基础配置。
- `Storage__S3__*`：S3 兼容存储配置，可用于 COS、Cloudflare R2 等。
- `BLOB_READ_WRITE_TOKEN` 或 `Storage__VercelBlob__Token`：Vercel Blob 读写令牌。
- `Storage__VercelBlob__*`：Vercel Blob 配置，当前项目建议将 `Storage__Provider` 设为 `VercelBlob`，并保持 `Access=public`，因为应用会直接把文件 URL 返回给前端用于下载和展示。

### Cloudflare R2 配置示例

项目已经支持把 `Storage__Provider` 设为 `R2`，内部仍复用现有的 S3 兼容实现。后端直传 R2 时建议至少配置下面这些变量：

```env
Storage__Provider=R2
Storage__S3__Bucket=your-r2-bucket
Storage__S3__Region=auto
Storage__S3__ServiceUrl=https://<ACCOUNT_ID>.r2.cloudflarestorage.com
Storage__S3__PublicBaseUrl=https://download.example.com
Storage__S3__AccessKey=<R2_ACCESS_KEY_ID>
Storage__S3__SecretKey=<R2_SECRET_ACCESS_KEY>
Storage__S3__KeyPrefix=downloader
Storage__S3__ForcePathStyle=true
```

说明：

- `Storage__S3__ServiceUrl` 使用 Cloudflare 官方的 R2 S3 兼容端点：`https://<ACCOUNT_ID>.r2.cloudflarestorage.com`。
- `Storage__S3__PublicBaseUrl` 必填。应用上传成功后会把文件 URL 直接返回给前端，所以这里应配置成你给 R2 绑定的自定义域名，或开发阶段临时使用的 `r2.dev` 公网地址。
- 生产环境更推荐给 Bucket 绑定自定义域名；`r2.dev` 适合开发测试，不建议长期承载生产下载流量。

### 从 Cloudflare R2 迁移到腾讯云 COS

项目提供了独立的迁移工具 [Downloader.Migrator](backend/Downloader.WebApi/Downloader.Migrator/README.md)：

```bash
cd backend/Downloader.WebApi
cp Downloader.Migrator/.env.migrate.example Downloader.Migrator/.env.migrate
# 填写 R2 / COS / 数据库配置后先 dry-run
dotnet run --project Downloader.Migrator -- --verbose
# 确认后执行
dotnet run --project Downloader.Migrator -- --apply
```

它会按数据库中的 `soft.SoftUrl` 和 `apps.IconUrl` 复制对象、校验大小（可选 SHA256），
并在 URL 映射发生变化时更新数据库。默认保留 R2 源文件，只有显式加 `--delete-source`
才会清理。继续使用 `/files` 代理时，让 COS 的 `KeyPrefix` 与 R2 保持一致即可不改数据库，
详细步骤、切换配置和回滚方式见 [迁移工具说明](backend/Downloader.WebApi/Downloader.Migrator/README.md)。
