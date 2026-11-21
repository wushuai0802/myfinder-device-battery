# 杀软误报与官方包核验

遇到杀软拦截时，先记录告警，再检查下载来源、清单签名和发行包哈希。全程保持实时防护开启。应由杀软厂商复核具体文件。

## 核对官方发行包

从本项目官方 Releases 页面下载同一版本的 Windows x64 ZIP、`SHA256SUMS.txt` 和 `SHA256SUMS.txt.sig`。程序正常更新会先用内置 RSA 公钥检查整个清单的签名，再按精确 ZIP 文件名校验 SHA-256。对手工下载，可按以下步骤在解包前完成同样的签名与哈希检查。[官方 Releases](https://github.com/wushuai0802/myfinder-device-battery/releases) · [项目更新校验](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/README.md#L271-L274)

1. 用一个临时目录保存三份原始附件。另从可信项目源码保存包含内置公钥的 `UpdateValidation.cs`，放在同一目录。这里使用可信源码作公钥来源，请从已确认的项目仓库或已信任的固定提交取得。[项目内置公钥](https://github.com/wushuai0802/myfinder-device-battery/blob/b70b621921c11ac65172e3d63fa50d64b2153b9d/scripts/UpdateValidation.cs#L17-L19)
2. 在 PowerShell 进入该目录。清单和签名保留下载时的原始字节。
3. 运行下面的只读核验。出现签名、文件名或哈希错误时，请重新从官方 Releases 下载对应版本。

```powershell
$ErrorActionPreference = 'Stop'
$archives = @(Get-ChildItem -File -Filter 'myfinder-device-battery-v*-windows-x64.zip')
if ($archives.Count -ne 1) { throw '核验目录需要恰好一个发行 ZIP。' }
$archive = $archives[0]
$folder = $archive.DirectoryName
$checksumBytes = [IO.File]::ReadAllBytes((Join-Path $folder 'SHA256SUMS.txt'))
$signature = [IO.File]::ReadAllBytes((Join-Path $folder 'SHA256SUMS.txt.sig'))
$source = [IO.File]::ReadAllText((Join-Path $folder 'UpdateValidation.cs'))
$key = [regex]::Match($source, 'internal const string PublicKeyXml = @"([^"]+)";')
if (-not $key.Success) { throw '未找到项目 RSA 公钥。' }
$rsa = [Security.Cryptography.RSACryptoServiceProvider]::new()
try {
    $rsa.PersistKeyInCsp = $false
    $rsa.FromXmlString($key.Groups[1].Value)
    $oid = [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256')
    if (-not $rsa.VerifyData($checksumBytes, $oid, $signature)) {
        throw '校验清单签名不匹配。'
    }
} finally { $rsa.Dispose() }

$pattern = '(?m)^([a-fA-F0-9]{64})[ \t]+\*?' + [regex]::Escape($archive.Name) + '[ \t]*\r?$'
$checksumText = [Text.Encoding]::UTF8.GetString($checksumBytes).TrimStart([char]0xFEFF)
$entry = [regex]::Matches($checksumText, $pattern)
if ($entry.Count -ne 1) { throw '已签名清单中没有唯一对应的 ZIP 文件名。' }
$actual = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash
if ($actual -ine $entry[0].Groups[1].Value) { throw 'ZIP SHA-256 不匹配。' }
"签名和 ZIP SHA-256 均通过。$actual"
```

这段命令已对 beta.21 测试版附件和该提交的公钥核验。Windows PowerShell 5.1 与 PowerShell 7 均验证通过。它核验 ZIP 及项目校验和签名。Windows 属性页的 Authenticode 是另一条签名链。

## 给杀软厂商提交误报

| 产品 | 官方入口和操作 |
| --- | --- |
| Windows Defender | 在 Microsoft Security Intelligence 提交文件，选择误报或错误判定场景，按页面提示上传被报出的公开发行文件。 [文件提交](https://www.microsoft.com/en-us/wdsi/filesubmission) |
| 卡巴斯基 | 在 Kaspersky Threat Intelligence Portal 查询 SHA-256 或提交样本分析。如果判定仍有疑问，通过产品官方支持请求误报复核，并附原有分析结果或提交编号。 [OpenTIP](https://opentip.kaspersky.com/) · [官方支持](https://support.kaspersky.com/b2c/) |
| 火绒 | 进入「病毒查杀问题反馈」，按「样本误报」分类提交，附官方下载地址、检测报告和被报出的公开文件资料。 [样本误报板块](https://bbs.huorong.cn/forum-44-1.html) |

准备程序版本、渠道、官方下载页面、告警文件的完整文件名、SHA-256、软件版本和病毒库时间、检测名称与出现步骤。需要截图时尽量只截检测项。按页面要求提供出自该发行包的文件，提交材料只含公开包和必要的脱敏信息。

## 本机处理

先等待厂商复核或病毒库更新。在来源、签名和 SHA-256 均核对通过，并确认确实需要立即使用时，可以按照厂商界面只信任已核验版本的独立安装目录。避免给下载目录、整个用户目录或系统盘设例外。核验失败或检测对象无法对应到官方包时，保留隔离并反馈核对结果。
