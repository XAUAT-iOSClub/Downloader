# Downloader 存储迁移工具（R2 -> 腾讯云 COS）

这个控制台工具把 Cloudflare R2 中**数据库实际引用**的安装包和应用图标复制到腾讯云 COS，
校验通过后按需改写数据库中的 `soft.SoftUrl` / `apps.IconUrl`。

它复用了后端的 `Downloader.Data` 模型和 EF Core 配置，因此 PostgreSQL 与 SQLite 都支持。
默认是 **dry-run**，只有显式加 `--apply` 才会复制对象和写数据库。

## 迁移范围

会处理：

- `soft` 表中 `SoftUrl` 指向的对象。
- `apps` 表中 `IconUrl` 指向的对象。
- 应用当前的代理 URL：`https://<API 域名>/files/<objectKey>`。
- S3/COS/R2 直链，以及裸对象键（例如 `downloader/uploads/software/a.apk`）。

不会处理：

- 数据库没有引用的 R2 孤儿对象。需要全桶迁移请单独用 `rclone`/`aws s3` 处理。
- `protocols.Context` 等长文本里可能内嵌的图片 URL。
- 外部 CDN、历史 Local/Vercel Blob 的 URL。它们会在报告里标记为 `Unresolved`。

## 为什么数据库可能“不需要改”

当前后端的公开 URL 是 `Storage__PublicBaseUrl` + `/files` + 对象键，例如：

```text
https://appapi.xauat.site/files/downloader/uploads/software/app.apk
```

数据库存的是这个代理 URL，而不是 R2 端点。只要 **COS 的 KeyPrefix 与 R2 一致**
（例如都为 `downloader`），迁移后同一个 `/files/...` 路径会自然指向 COS，
数据库里的字符串不需要变化，工具会把这些记录标记为 `Unchanged`。

只有在以下情况才会真正写库：

- 目标 KeyPrefix 与源不同，对象键发生变化。
- 修改了 `MIGRATION__NEW_PUBLIC_BASE`，例如改用 COS 自定义域名。
- 历史记录不是当前公开前缀，但能识别出对象键。

工具始终先复制并校验，再更新数据库；对象失败时对应记录会被标记为 `Blocked`，不会写库。

## 前置准备

1. 在腾讯云 COS 创建 Bucket，建议与后端同地域，例如 `ap-guangzhou`。
2. 准备 COS 的 SecretId / SecretKey，至少需要：
   - `HeadObject` / `GetObject`
   - `PutObject`、`InitiateMultipartUpload`、`UploadPart`、`CompleteMultipartUpload`、`AbortMultipartUpload`
   - 如果启用 `--delete-source`：`DeleteObject`
3. Bucket 可以保持**私有**。应用走 `/files` 代理下载时由后端使用密钥读取，不需要公开读。
4. 备份数据库（生产环境务必执行）：

   ```bash
   pg_dump "Host=...;Database=downloader;Username=...;Password=..." \
     -Fc -f downloader-before-cos-$(date +%Y%m%d-%H%M).dump
   ```

5. 准备配置文件：

   ```bash
   cp backend/Downloader.WebApi/Downloader.Migrator/.env.migrate.example \
      backend/Downloader.WebApi/Downloader.Migrator/.env.migrate
   ```

   `.env.migrate` 已被 `.gitignore` 忽略，不要提交真实密钥。

配置查找顺序：`--config` > `MIGRATION_CONFIG` 环境变量 > 从当前目录向上找 `.env.migrate` >
从程序目录向上找 `.env.migrate`。状态、报告、日志的相对路径以配置文件所在目录为基准。

## 配置说明

```env
# 数据库
SQL=Host=...;Port=5432;Username=...;Database=downloader;Password=...

# 源：Cloudflare R2
SOURCE__ENDPOINT=https://<ACCOUNT_ID>.r2.cloudflarestorage.com
SOURCE__BUCKET=app
SOURCE__REGION=auto
SOURCE__ACCESS_KEY=<R2_ACCESS_KEY_ID>
SOURCE__SECRET_KEY=<R2_SECRET_ACCESS_KEY>
SOURCE__KEY_PREFIX=downloader
SOURCE__FORCE_PATH_STYLE=true

# 目标：腾讯云 COS
TARGET__ENDPOINT=https://cos.ap-guangzhou.myqcloud.com
TARGET__BUCKET=<BUCKET_NAME>-<APPID>
TARGET__REGION=ap-guangzhou
TARGET__ACCESS_KEY=<COS_SECRET_ID>
TARGET__SECRET_KEY=<COS_SECRET_KEY>
TARGET__KEY_PREFIX=downloader
TARGET__FORCE_PATH_STYLE=false
TARGET__DISABLE_CHECKSUM=true

# 数据库中的公开 URL
MIGRATION__OLD_PUBLIC_BASE=https://appapi.xauat.site/files
MIGRATION__NEW_PUBLIC_BASE=https://appapi.xauat.site/files

# 行为
MIGRATION__CONCURRENCY=4
MIGRATION__VERIFY=size
MIGRATION__UPDATE_DATABASE=true
MIGRATION__DELETE_SOURCE=false
```

关键点：

- **KeyPrefix 保持一致**是“不改数据库、直接切换后端”的前提。
- 缺失的 `SOURCE__*` 字段会回退到后端的 `Storage__S3__*`，方便直接复用现有 R2 配置；
  如果源和目标不是同一套 R2，请显式写全 `SOURCE__*`，避免误用。
- COS 的 endpoint 不要带 bucket，bucket 单独写在 `TARGET__BUCKET`。
  COS Bucket 名带 APPID 后缀，例如 `downloader-1250000000`。
- `TARGET__DISABLE_CHECKSUM=true` 用于绕过 AWS SDK v4 默认的 CRC 校验头，
  COS 对这类头支持不稳定；如果出现签名/校验错误先确认这一项。
- `MIGRATION__VERIFY`：
  - `size`（默认）：源和目标大小一致即通过，成本最低。
  - `sha256`：额外完整读取源和目标各一次，最严格但耗时、耗流量。
  - `none`：只依赖上传成功和 HEAD 大小检查。
- `MIGRATION__OLD_PUBLIC_BASE` 可以分号分隔多个前缀；`MIGRATION__TARGET_PUBLIC_BASE`
  用于识别已经指向目标域的旧记录。
- 如果 R2 使用自定义域名，可把 `MIGRATION__OLD_PUBLIC_BASE` 设为
  `https://download.example.com`（如有 `/files` 代理路径则带上）。

## 使用

在 `backend/Downloader.WebApi` 目录执行：

```bash
# 1. 查看计划和即将发生的 URL 变化（不写任何东西）
dotnet run --project Downloader.Migrator -- --verbose

# 2. 先搬 1 个对象，严格校验，确认链路
dotnet run --project Downloader.Migrator -- --apply --limit 1 --verify sha256 --verbose

# 3. 全量执行
dotnet run --project Downloader.Migrator -- --apply

# 4. 只复制对象，不动数据库
dotnet run --project Downloader.Migrator -- --apply --no-db

# 5. 对象已经复制好，只校验目标并改数据库
dotnet run --project Downloader.Migrator -- --apply --no-copy

# 6. 迁移稳定后清理 R2 源对象（会先 HEAD 校验目标，再删除大小一致的源对象）
dotnet run --project Downloader.Migrator -- --apply --delete-source
```

源端删除被刻意放在数据库更新之后：只有该对象关联的所有数据库记录都处于
`Updated` 或 `Unchanged` 时才会删源；任何一条记录 `Blocked`、`Failed` 或 `TooLong`
都会跳过删除，避免出现“R2 已删、数据库还指向旧 KeyPrefix”的情况。
删除前还会比较源/目标大小，大小不一致时不会删除。
`--no-db --delete-source` 会跳过数据库确认，只应在明确不需要改库时使用。

即使数据库 URL 已经改写成目标域名/KeyPrefix，再次执行 `--apply --delete-source`
也能从目标键反推源键并完成清理；如果目标键不符合目标前缀、无法安全反推，工具会跳过删除。
删除前建议用 `--verify sha256`，因为默认的 `size` 只比较大小，不比较内容。

常用过滤：

```bash
# 只处理安装包 / 只处理图标
dotnet run --project Downloader.Migrator -- --only soft
dotnet run --project Downloader.Migrator -- --only icons

# 只处理某个 App
dotnet run --project Downloader.Migrator -- --app-id <appId> --verbose

# 指定配置文件
dotnet run --project Downloader.Migrator -- --config /path/to/.env.migrate

# 忽略历史遗留的外部 URL，不让它们影响退出码
dotnet run --project Downloader.Migrator -- --apply --ignore-unresolved
```

退出码：`0` 全部成功；`1` 有冲突/缺失/失败/未识别 URL；`2` 配置或参数错误；
`130` 用户按 Ctrl+C 中断。中断后已复制的对象和已更新的记录会保留，重新运行即可继续。

## 输出文件

默认写在配置文件所在目录：

- `migration-state.json`：每个对象和数据库记录的尝试次数、状态、错误，便于审计和断点排查。
- `migration-report-YYYYMMDD-HHmmss.csv` 或 `migration-report.csv`：逐条记录的报告，
  包含旧 URL、新 URL、源键、目标键、大小、对象状态、数据库状态。
- `migration.log`：追加写入的运行日志（不含密钥）。

幂等判断以目标对象 HEAD 结果为准：目标存在且大小一致就跳过，不依赖状态文件。
因此重复执行是安全的；状态文件损坏时会自动备份并重新开始。

## 切换后端到 COS

迁移和校验完成后，修改 [backend/Downloader.WebApi/.env](../.env)
中的存储配置。继续使用 `/files` 代理时，`Storage__S3__PublicBaseUrl` 留空：

```env
Storage__Provider=S3
Storage__PublicBaseUrl=https://appapi.xauat.site
Storage__S3__Bucket=<BUCKET_NAME>-<APPID>
Storage__S3__Region=ap-guangzhou
Storage__S3__ServiceUrl=https://cos.ap-guangzhou.myqcloud.com
Storage__S3__PublicBaseUrl=
Storage__S3__AccessKey=<COS_SECRET_ID>
Storage__S3__SecretKey=<COS_SECRET_KEY>
Storage__S3__KeyPrefix=downloader
Storage__S3__ForcePathStyle=false
# 后端上传新文件到 COS 时关闭 AWS SDK v4 默认 CRC 校验头
Storage__S3__DisableChecksum=true
```

如果希望数据库直接返回 COS 自定义域名而不是 API 代理：

```env
Storage__S3__PublicBaseUrl=https://download.example.com
```

此时要同步修改迁移配置的 `MIGRATION__NEW_PUBLIC_BASE`，并在迁移时写库；
还需要在 COS 配置 CORS 和公网读权限。

切换后建议抽查：

```bash
curl -I "https://appapi.xauat.site/files/downloader/uploads/software/<file>"
curl -r 0-1023 "https://appapi.xauat.site/files/downloader/uploads/software/<file>" -o /tmp/head.bin
```

确认状态码、`Content-Length`、`Content-Type` 和 Range 下载都正常，再观察一段时间后清理 R2。

## 回滚

- **KeyPrefix 和公开 URL 都没变**：把后端 `.env` 改回 R2 配置并重启即可，数据库无需回滚。
- **迁移时改写了数据库 URL**：需要恢复迁移前的数据库备份，或按反向映射修正 URL。
  因此建议首次迁移保持 KeyPrefix 不变，避免不必要的数据库变更。

## 常见问题

| 现象 | 排查方向 |
| --- | --- |
| `403 Forbidden` / `AccessDenied` | SecretId/SecretKey、Bucket 名（含 APPID）、地域、子账号权限 |
| `404` / `NoSuchKey` | endpoint 与 bucket 地域是否匹配；KeyPrefix 是否一致 |
| `InvalidRequest`、校验头相关错误 | 确认 `TARGET__DISABLE_CHECKSUM=true`，必要时设置 `TARGET__DISABLE_PAYLOAD_SIGNING=true` |
| 分片数量超限 | 增大 `MIGRATION__PART_SIZE_MB`（COS 单次分片 1 MB - 5 GB，最多 10000 片；工具默认 16 MB，上限 512 MB） |
| 大量 `Unresolved` | 检查 `MIGRATION__OLD_PUBLIC_BASE` 是否包含了协议、域名和 `/files` 路径 |
| 数据库连接失败 | 先确认 `SQL`；SQLite 路径相对于进程工作目录 |
| `TooLong` | 新 URL 超过 `SoftUrl`/`IconUrl` 的 512 字符上限，检查公开域名和 KeyPrefix 长度 |
| 大小相同但 SHA256 不一致 | 目标对象可能损坏；用 `--verify sha256 --force` 重新复制 |
| 复制很慢 | 提高 `MIGRATION__CONCURRENCY`（1-32），或把工具部署到离 R2/COS 更近的机器 |

## 安全建议

- `.env.migrate`、状态文件、日志都不要提交到仓库。
- 迁移完成后轮换 R2/COS 密钥，或至少撤销临时密钥。
- 生产迁移前先备份数据库，并先用 `--limit` 小批量验证。
- 默认不删除源对象；确认 COS 上的下载、Range 请求、图标展示都正常后再考虑清理。
- 大文件走 multipart 上传；如果进程中断，COS 上可能留下未完成的分片上传，
  建议给 Bucket 配置“清理未完成分片”的生命周期规则。
